#include "media/media_inspector.h"
#include "media/video_converter.h"
#include "foundation/internal.h"
#include "foundation/sha256.h"
#include "foundation/residue_fingerprint.h"
#include "protocols/apple.h"
#include "protocols/samsung_sef.h"
#include "containers/mp4_strip.h"
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
    std::string_view text_value;
    size_t content_start{0};
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

static std::string_view canonical_namespace_uri(std::string_view value) noexcept {
        while (!value.empty() && std::isspace(static_cast<unsigned char>(value.front()))) value.remove_prefix(1);
        while (!value.empty() && std::isspace(static_cast<unsigned char>(value.back()))) value.remove_suffix(1);
        if (value.size() >= 2 && ((value.front() == '"' && value.back() == '"') ||
                (value.front() == '\'' && value.back() == '\''))) {
            value.remove_prefix(1);
            value.remove_suffix(1);
            while (!value.empty() && std::isspace(static_cast<unsigned char>(value.front()))) value.remove_prefix(1);
            while (!value.empty() && std::isspace(static_cast<unsigned char>(value.back()))) value.remove_suffix(1);
        }
        if (value.size() >= 12 && value.substr(0, 6) == "&quot;" && value.substr(value.size() - 6) == "&quot;") {
            value.remove_prefix(6);
            value.remove_suffix(6);
        } else if (value.size() >= 12 && value.substr(0, 6) == "&apos;" && value.substr(value.size() - 6) == "&apos;") {
            value.remove_prefix(6);
            value.remove_suffix(6);
        }
        while (!value.empty() && std::isspace(static_cast<unsigned char>(value.front()))) value.remove_prefix(1);
        while (!value.empty() && std::isspace(static_cast<unsigned char>(value.back()))) value.remove_suffix(1);
        static constexpr std::string_view known[] = {
            google_camera_namespace, google_container_namespace, google_item_namespace,
            oppo_camera_namespace, vivo_camera_namespace
        };
        for (const auto canonical : known) {
            if (value == canonical) return canonical;
            if (value.size() == canonical.size() + 1 && value.substr(0, 8) == "https://" &&
                canonical.substr(0, 7) == "http://" && value.substr(8) == canonical.substr(7)) {
                return canonical;
            }
        }
        return value;
}

static void set_namespace_binding(std::vector<namespace_binding>& bindings,
    std::string_view prefix, std::string_view uri) {
    uri = canonical_namespace_uri(uri);
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
            if (nodes[top_idx].children.empty() && nodes[top_idx].content_start <= p) {
                std::string_view value = xml.substr(nodes[top_idx].content_start, p - nodes[top_idx].content_start);
                while (!value.empty() && std::isspace(static_cast<unsigned char>(value.front()))) value.remove_prefix(1);
                while (!value.empty() && std::isspace(static_cast<unsigned char>(value.back()))) value.remove_suffix(1);
                nodes[top_idx].text_value = value;
            }
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
        nodes.push_back({ new_idx, parent_idx, tag_name, tag_uri, std::move(attributes), {}, {}, tag_end });
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

static bool has_attribute_name_in_nodes(const std::vector<xmp_node>& nodes,
    std::string_view uri, std::string_view local) noexcept {
    for (const auto& node : nodes) {
        if (node_is(node, uri, local)) return true;
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
        if (node_is(node, uri, local)) {
            if (node.text_value.empty()) return -1;
            uint64_t val = 0;
            if (!parse_u64_exact(node.text_value, val)) return -1;
            if (found) return -1;
            found = true;
            current = val;
        }
        for (const auto& attr : node.attributes) {
            if (local_name(attr.name) == local && attr.resolved_uri == uri) {
                uint64_t val = 0;
                if (!parse_u64_exact(attr.value, val)) return -1;
                if (found) return -1;
                found = true;
                current = val;
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
        if (node_is(node, uri, local)) {
            if (node.text_value.empty()) return -1;
            int64_t val = 0;
            if (!parse_i64_exact(node.text_value, val)) return -1;
            if (found) return -1;
            found = true;
            current = val;
        }
        for (const auto& attr : node.attributes) {
            if (local_name(attr.name) == local && attr.resolved_uri == uri) {
                int64_t val = 0;
                if (!parse_i64_exact(attr.value, val)) return -1;
                if (found) return -1;
                found = true;
                current = val;
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
        if (node_is(node, uri, local)) {
            if (node.text_value.empty() || found) return -1;
            found = true;
            current = node.text_value;
        }
        for (const auto& attr : node.attributes) {
            if (local_name(attr.name) == local && attr.resolved_uri == uri) {
                if (found) return -1;
                found = true;
                current = attr.value;
            }
        }
    }
    if (found) {
        out_value = current;
        return 1;
    }
    return 0;
}

static std::string get_attribute_value_in_nodes(const std::vector<xmp_node>& nodes,
    std::string_view uri, std::string_view local) {
    std::string_view val;
    if (get_global_attribute_string(nodes, uri, local, val) == 1) {
        return std::string(val);
    }
    return {};
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

enum class node_property_result : uint8_t {
    absent = 0,
    present = 1,
    malformed = 2,
};

// A property may be encoded either as a namespace-qualified attribute on the
// owner element or as a namespace-qualified child element containing text.
// These are alternate encodings, not two independent values: seeing both (or
// seeing the same form more than once) is a shadow/duplicate declaration and
// must not be resolved by first-match-wins.
static node_property_result get_node_property_value_checked(
    const std::vector<xmp_node>& nodes, const xmp_node& node,
    std::string_view uri, std::string_view local, std::string_view& out_value) noexcept;

static node_property_result get_node_property_value_checked(
    const std::vector<xmp_node>& nodes, const xmp_node& node,
    std::string_view uri, std::string_view local, std::string_view& out_value) noexcept {
    bool found_attribute = false;
    bool found_element = false;
    std::string_view attribute_value;
    std::string_view element_value;
    for (const auto& attr : node.attributes) {
        if (local_name(attr.name) != local || attr.resolved_uri != uri) continue;
        if (found_attribute) return node_property_result::malformed;
        found_attribute = true;
        attribute_value = attr.value;
    }
    for (size_t child_index : node.children) {
        if (child_index >= nodes.size()) return node_property_result::malformed;
        const auto& child = nodes[child_index];
        if (!node_is(child, uri, local)) continue;
        if (found_element || !child.children.empty() || child.text_value.empty()) {
            return node_property_result::malformed;
        }
        found_element = true;
        element_value = child.text_value;
    }
    if (found_attribute && found_element) return node_property_result::malformed;
    if (!found_attribute && !found_element) return node_property_result::absent;
    out_value = found_attribute ? attribute_value : element_value;
    return node_property_result::present;
}

static bool xmp_node_is_descendant_of(const std::vector<xmp_node>& nodes,
    size_t node_index, size_t ancestor_index) noexcept;

static bool find_container_directory(const std::vector<xmp_node>& nodes,
    container_directory_info& out_dir, size_t owner_description = k_no_parent) {
    out_dir = {};
    const xmp_node* dir_node = nullptr;
    for (const auto& node : nodes) {
        if (node_is(node, google_container_namespace, "Directory") &&
            (owner_description == k_no_parent ||
             xmp_node_is_descendant_of(nodes, node.node_index, owner_description))) {
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
        if (seq_item_idx >= nodes.size()) return false;
        const xmp_node& seq_item = nodes[seq_item_idx];

        const xmp_node* item_node = nullptr;
        if (node_is(seq_item, google_container_namespace, "Item")) {
            item_node = &seq_item;
        } else if (node_is(seq_item, rdf_namespace, "li")) {
            size_t container_item_count = 0;
            for (size_t li_child_idx : seq_item.children) {
                if (li_child_idx < nodes.size() && node_is(nodes[li_child_idx], google_container_namespace, "Item")) {
                    ++container_item_count;
                    item_node = &nodes[li_child_idx];
                } else if (li_child_idx >= nodes.size()) {
                    return false;
                }
            }
            if (container_item_count > 1) return false;
            if (!item_node) {
                std::string_view ignored;
                const auto semantic_result = get_node_property_value_checked(
                    nodes, seq_item, google_item_namespace, "Semantic", ignored);
                const auto mime_result = get_node_property_value_checked(
                    nodes, seq_item, google_item_namespace, "Mime", ignored);
                if (semantic_result == node_property_result::malformed ||
                    mime_result == node_property_result::malformed) {
                    return false;
                }
                if (semantic_result == node_property_result::present ||
                    mime_result == node_property_result::present) {
                    item_node = &seq_item;
                }
            }
        } else {
            // Every member of the authoritative rdf:Seq must be a direct
            // Container:Item or an rdf:li containing exactly one such item.
            return false;
        }
        // Every rdf:li in the authoritative sequence must resolve to exactly
        // one Container:Item. Ignoring a malformed/shadow child lets an
        // attacker hide duplicate declarations behind a valid first item.
        if (!item_node) return false;

        container_item_info info{};
        info.node_index = item_node->node_index;
        auto semantic_result = get_node_property_value_checked(
            nodes, *item_node, google_item_namespace, "Semantic", info.semantic);
        if (semantic_result == node_property_result::malformed) return false;
        if (semantic_result == node_property_result::absent && item_node != &seq_item) {
            const auto parent_result = get_node_property_value_checked(
                nodes, seq_item, google_item_namespace, "Semantic", info.semantic);
            if (parent_result == node_property_result::malformed) return false;
            semantic_result = parent_result;
        } else if (semantic_result == node_property_result::present && item_node != &seq_item) {
            std::string_view shadow_value;
            const auto parent_result = get_node_property_value_checked(
                nodes, seq_item, google_item_namespace, "Semantic", shadow_value);
            if (parent_result != node_property_result::absent) return false;
        }
        auto mime_result = get_node_property_value_checked(
            nodes, *item_node, google_item_namespace, "Mime", info.mime);
        if (mime_result == node_property_result::malformed) return false;
        if (mime_result == node_property_result::absent && item_node != &seq_item) {
            const auto parent_result = get_node_property_value_checked(
                nodes, seq_item, google_item_namespace, "Mime", info.mime);
            if (parent_result == node_property_result::malformed) return false;
            mime_result = parent_result;
        } else if (mime_result == node_property_result::present && item_node != &seq_item) {
            std::string_view shadow_value;
            const auto parent_result = get_node_property_value_checked(
                nodes, seq_item, google_item_namespace, "Mime", shadow_value);
            if (parent_result != node_property_result::absent) return false;
        }
        std::string_view len_str;
        auto length_result = get_node_property_value_checked(
            nodes, *item_node, google_item_namespace, "Length", len_str);
        if (length_result == node_property_result::malformed) return false;
        if (length_result == node_property_result::absent && item_node != &seq_item) {
            const auto parent_result = get_node_property_value_checked(
                nodes, seq_item, google_item_namespace, "Length", len_str);
            if (parent_result == node_property_result::malformed) return false;
            length_result = parent_result;
        } else if (length_result == node_property_result::present && item_node != &seq_item) {
            std::string_view shadow_value;
            const auto parent_result = get_node_property_value_checked(
                nodes, seq_item, google_item_namespace, "Length", shadow_value);
            if (parent_result != node_property_result::absent) return false;
        }
        if (length_result == node_property_result::present) {
            if (parse_u64_exact(len_str, info.length)) {
                info.has_length = true;
            } else {
                info.malformed_length = true;
            }
        }
        std::string_view pad_str;
        auto padding_result = get_node_property_value_checked(
            nodes, *item_node, google_item_namespace, "Padding", pad_str);
        if (padding_result == node_property_result::malformed) return false;
        if (padding_result == node_property_result::absent && item_node != &seq_item) {
            const auto parent_result = get_node_property_value_checked(
                nodes, seq_item, google_item_namespace, "Padding", pad_str);
            if (parent_result == node_property_result::malformed) return false;
            padding_result = parent_result;
        } else if (padding_result == node_property_result::present && item_node != &seq_item) {
            std::string_view shadow_value;
            const auto parent_result = get_node_property_value_checked(
                nodes, seq_item, google_item_namespace, "Padding", shadow_value);
            if (parent_result != node_property_result::absent) return false;
        }
        if (padding_result == node_property_result::present) {
            if (parse_u64_exact(pad_str, info.padding)) {
                info.has_padding = true;
            } else {
                info.malformed_padding = true;
            }
        }
        if (info.semantic.empty() || info.mime.empty()) return false;
        out_dir.items.push_back(info);
    }

    return !out_dir.items.empty();
}

static bool xmp_node_is_descendant_of(const std::vector<xmp_node>& nodes,
    size_t node_index, size_t ancestor_index) noexcept {
    if (node_index >= nodes.size() || ancestor_index >= nodes.size()) return false;
    size_t current = node_index;
    while (current != k_no_parent) {
        if (current == ancestor_index) return true;
        if (current >= nodes.size()) return false;
        current = nodes[current].parent_index;
    }
    return false;
}

static bool xmp_node_is_protocol_marker(const xmp_node& node) noexcept {
    const auto uri = node.resolved_uri;
    const auto local = local_name(node.tag_name);
    if (uri == google_container_namespace && (local == "Directory" || local == "Item")) return true;
    if (uri == google_item_namespace && (local == "Semantic" || local == "Mime" ||
            local == "Length" || local == "Padding")) return true;
    if ((uri == google_camera_namespace || uri == oppo_camera_namespace || uri == vivo_camera_namespace) &&
        (local == "MotionPhoto" || local == "MotionPhotoVersion" ||
         local == "MotionPhotoPresentationTimestampUs" || local == "MicroVideo" ||
         local == "MicroVideoVersion" || local == "MicroVideoOffset" ||
         local == "MotionPhotoOwner" || local == "VideoLength" ||
         local == "OLivePhotoVersion" || local == "VMotionPhotoVersion" ||
         local == "VMotionPhotoSource" || local == "VMotionPhotoFlags" ||
         local == "VMediaKitVersion")) return true;
    for (const auto& attribute : node.attributes) {
        const auto attr_uri = attribute.resolved_uri;
        const auto attr_local = local_name(attribute.name);
        if (attr_uri == google_container_namespace && (attr_local == "Directory" || attr_local == "Item")) return true;
        if (attr_uri == google_item_namespace && (attr_local == "Semantic" || attr_local == "Mime" ||
                attr_local == "Length" || attr_local == "Padding")) return true;
        if ((attr_uri == google_camera_namespace || attr_uri == oppo_camera_namespace || attr_uri == vivo_camera_namespace) &&
            (attr_local == "MotionPhoto" || attr_local == "MotionPhotoVersion" ||
             attr_local == "MotionPhotoPresentationTimestampUs" || attr_local == "MicroVideo" ||
             attr_local == "MicroVideoVersion" || attr_local == "MicroVideoOffset" ||
             attr_local == "MotionPhotoOwner" || attr_local == "VideoLength" ||
             attr_local == "OLivePhotoVersion" || attr_local == "VMotionPhotoVersion" ||
             attr_local == "VMotionPhotoSource" || attr_local == "VMotionPhotoFlags" ||
             attr_local == "VMediaKitVersion")) return true;
    }
    return false;
}

// XMP protocol properties and Container:Directory are authoritative only
// inside one rdf:Description.  Global first-match attribute reads must never
// combine fields from separate descriptions or from an unowned directory.
static bool validate_xmp_protocol_ownership(const std::vector<xmp_node>& nodes,
    size_t& out_owner_description) noexcept {
    out_owner_description = k_no_parent;
    std::vector<size_t> descriptions;
    for (const auto& node : nodes) {
        if (node_is(node, rdf_namespace, "Description")) descriptions.push_back(node.node_index);
    }
    if (descriptions.empty()) {
        for (const auto& node : nodes) {
            if (xmp_node_is_protocol_marker(node)) return false;
        }
        return true;
    }

    std::vector<size_t> owners;
    for (const size_t description : descriptions) {
        bool has_marker = false;
        for (const auto& node : nodes) {
            if (xmp_node_is_descendant_of(nodes, node.node_index, description) &&
                xmp_node_is_protocol_marker(node)) {
                has_marker = true;
                break;
            }
        }
        if (has_marker) owners.push_back(description);
    }
    if (owners.size() > 1) return false;
    if (owners.empty()) return true;

    const size_t owner = owners.front();
    for (const auto& node : nodes) {
        if (xmp_node_is_protocol_marker(node) &&
            !xmp_node_is_descendant_of(nodes, node.node_index, owner)) return false;
    }
    out_owner_description = owner;
    return true;
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

static std::string extract_xmp_string(lpb_context* context, const std::vector<uint8_t>& data,
    lpb_image_container container, bool* out_invalid = nullptr) {
    if (out_invalid) *out_invalid = false;
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
        size_t range_count = 0;
        const lpb_result count_result = lpb_heif_enumerate_xmp_items(
            context, data.data(), data.size(), nullptr, 0, &range_count);
        if (count_result != LPB_RESULT_BUFFER_TOO_SMALL || range_count == 0) return {};

        std::vector<lpb_media_range> ranges(range_count);
        if (lpb_heif_enumerate_xmp_items(context, data.data(), data.size(),
                ranges.data(), ranges.size(), &range_count) != LPB_RESULT_OK) {
            if (out_invalid) *out_invalid = true;
            return {};
        }

        std::string fallback;
        std::string relevant_packet;
        size_t motion_packet_count = 0;
        size_t relevant_packet_count = 0;
        for (const auto& range : ranges) {
            if (range.offset > data.size() || range.length > data.size() - static_cast<size_t>(range.offset)) {
                if (out_invalid) *out_invalid = true;
                return {};
            }
            const std::string packet = extract_xml_fragment(std::string_view(
                reinterpret_cast<const char*>(data.data() + static_cast<size_t>(range.offset)),
                static_cast<size_t>(range.length)));
            if (packet.empty()) {
                if (out_invalid) *out_invalid = true;
                return {};
            }

            if (fallback.empty()) fallback = packet;
            std::vector<xmp_node> packet_nodes;
            if (!scan_xmp_tree(packet, packet_nodes)) {
                if (out_invalid) *out_invalid = true;
                return {};
            }
            container_directory_info directory;
            bool has_motion_item = false;
            if (find_container_directory(packet_nodes, directory)) {
                for (const auto& item : directory.items) {
                    if (item.semantic == "MotionPhoto") {
                        has_motion_item = true;
                        break;
                    }
                }
            }
            const bool has_motion_attribute = has_attribute_name_in_nodes(
                packet_nodes, google_camera_namespace, "MotionPhoto");
            bool has_relevant_protocol_property = has_motion_attribute || has_motion_item;
            for (const auto& node : packet_nodes) {
                const auto is_relevant_local = [](std::string_view local) noexcept {
                    return local == "MotionPhoto" || local == "MotionPhotoVersion" ||
                        local == "MotionPhotoPresentationTimestampUs" ||
                        local == "MicroVideo" || local == "MicroVideoVersion" ||
                        local == "MicroVideoOffset" || local == "Directory" ||
                        local == "Semantic" || local == "Mime" || local == "Length" ||
                        local == "Padding";
                };
                if ((node.resolved_uri == google_camera_namespace ||
                        node.resolved_uri == google_container_namespace ||
                        node.resolved_uri == google_item_namespace ||
                        node.resolved_uri == vivo_camera_namespace) &&
                    is_relevant_local(local_name(node.tag_name))) {
                    has_relevant_protocol_property = true;
                }
                for (const auto& attribute : node.attributes) {
                    if ((attribute.resolved_uri == google_camera_namespace ||
                            attribute.resolved_uri == google_container_namespace ||
                            attribute.resolved_uri == google_item_namespace ||
                            attribute.resolved_uri == vivo_camera_namespace) &&
                        is_relevant_local(local_name(attribute.name))) {
                        has_relevant_protocol_property = true;
                    }
                }
            }
            if (has_relevant_protocol_property) {
                ++relevant_packet_count;
                relevant_packet = packet;
            }
            if (has_motion_attribute || has_motion_item) {
                ++motion_packet_count;
            }
        }

        // Multiple RDF items are legal in HEIF.  A MotionPhoto packet is
        // authoritative only when exactly one candidate contains the formal
        // GCamera/Container motion declaration; otherwise fail closed.
        if (motion_packet_count > 1 || relevant_packet_count > 1 ||
            motion_packet_count > relevant_packet_count) {
            if (out_invalid) *out_invalid = true;
            return {};
        }
        // A single relevant packet is returned even when it is incomplete.
        // The protocol-specific validator must then reject the missing formal
        // fields instead of silently falling back to an unrelated first item.
        return relevant_packet_count == 1 ? relevant_packet : fallback;
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

// Neutral Samsung JPEGs may retain non-live SEF metadata after the motion
// entry is removed.  Validate that bounded trailer as a real SEF directory;
// this is deliberately not a byte-pattern allowance for arbitrary JPEG tail
// data.  Motion entries are rejected because they would be live ownership.
static bool is_valid_non_motion_sef_range(
    const std::vector<uint8_t>& data, uint64_t offset, uint64_t length) noexcept
{
    if (offset > data.size() || length < 16 || length > data.size() - static_cast<size_t>(offset)) return false;
    const size_t start = static_cast<size_t>(offset);
    const size_t end = start + static_cast<size_t>(length);
    if (std::memcmp(data.data() + end - 4, "SEFT", 4) != 0) return false;
    const auto read_le16 = [&](size_t at) noexcept -> uint16_t {
        return static_cast<uint16_t>(data[at]) | (static_cast<uint16_t>(data[at + 1]) << 8);
    };
    const auto read_le32 = [&](size_t at) noexcept -> uint32_t {
        return static_cast<uint32_t>(data[at]) |
            (static_cast<uint32_t>(data[at + 1]) << 8) |
            (static_cast<uint32_t>(data[at + 2]) << 16) |
            (static_cast<uint32_t>(data[at + 3]) << 24);
    };
    const size_t footer = end - 8;
    const uint32_t total_size = read_le32(footer);
    if (total_size < 12 || static_cast<uint64_t>(total_size) > length - 8) return false;
    const size_t sefh = footer - static_cast<size_t>(total_size);
    if (sefh < start || std::memcmp(data.data() + sefh, "SEFH", 4) != 0 || sefh + 12 > footer) return false;
    const uint32_t count = read_le32(sefh + 8);
    if (count > (footer - (sefh + 12)) / 12 || sefh + 12 + static_cast<size_t>(count) * 12 != footer) return false;

    std::vector<std::pair<size_t, size_t>> payloads;
    payloads.reserve(count);
    for (uint32_t i = 0; i < count; ++i) {
        const size_t entry = sefh + 12 + static_cast<size_t>(i) * 12;
        const uint16_t prefix = read_le16(entry);
        const uint16_t marker = read_le16(entry + 2);
        const uint32_t back_offset = read_le32(entry + 4);
        const uint32_t payload_size = read_le32(entry + 8);
        if (marker == 0x0A30 || marker == 0x0A31 || payload_size < 8 ||
            static_cast<uint64_t>(back_offset) > static_cast<uint64_t>(sefh - start) ||
            payload_size > back_offset) return false;
        const size_t payload = sefh - static_cast<size_t>(back_offset);
        const size_t payload_end = payload + static_cast<size_t>(payload_size);
        if (payload < start || payload_end > sefh ||
            read_le16(payload) != prefix || read_le16(payload + 2) != marker) return false;
        const uint32_t name_size = read_le32(payload + 4);
        if (name_size > payload_size - 8) return false;
        for (const auto& prior : payloads) {
            if (payload < prior.second && prior.first < payload_end) return false;
        }
        payloads.emplace_back(payload, payload_end);
    }
    std::sort(payloads.begin(), payloads.end());
    size_t cursor = start;
    for (const auto& payload : payloads) {
        if (payload.first != cursor) return false;
        cursor = payload.second;
    }
    return cursor == sefh;
}

static bool check_samsung_sef_heic(
    const std::vector<uint8_t>& data,
    uint64_t& out_video_offset,
    uint64_t& out_video_len,
    isobmff_box_header& out_sefd_box)
{
    out_sefd_box = {};
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
        out_sefd_box = sefd_box;
    }
    return found_motion;
}

static bool check_vivo_x300(
    const std::vector<xmp_node>& nodes,
    size_t owner_description,
    uint64_t file_size,
    uint64_t& out_primary_len,
    uint64_t& out_gm_offset,
    uint64_t& out_gm_len,
    uint64_t& out_video_offset,
    uint64_t& out_video_len)
{
    container_directory_info dir;
    if (!find_container_directory(nodes, dir, owner_description) ||
        (dir.items.size() != 2 && dir.items.size() != 3)) {
        return false;
    }

    const auto& item0 = dir.items[0];
    const auto& item1 = dir.items[1];

    if (item0.semantic != "Primary" || item0.mime != "image/jpeg" ||
        item0.has_length || item0.has_padding || item0.malformed_length || item0.malformed_padding) {
        return false;
    }
    const container_item_info* gainmap = nullptr;
    const container_item_info* motion = nullptr;
    if (dir.items.size() == 2) {
        if (item1.semantic != "MotionPhoto" || item1.mime != "video/mp4" ||
            !item1.has_length || item1.malformed_length || item1.length == 0 ||
            !item1.has_padding || item1.malformed_padding || item1.padding != 0) {
            return false;
        }
        motion = &item1;
    } else {
        const auto& item2 = dir.items[2];
        if (item1.semantic != "GainMap" || item1.mime != "image/jpeg" ||
            !item1.has_length || item1.malformed_length || item1.length == 0 ||
            item1.has_padding || item1.malformed_padding ||
            item2.semantic != "MotionPhoto" || item2.mime != "video/mp4" ||
            !item2.has_length || item2.malformed_length || item2.length == 0 ||
            !item2.has_padding || item2.malformed_padding || item2.padding != 0) {
            return false;
        }
        gainmap = &item1;
        motion = &item2;
    }

    if (motion == nullptr || motion->length == 0) {
        return false;
    }

    const uint64_t gainmap_length = gainmap != nullptr ? gainmap->length : 0;
    const uint64_t motion_length = motion->length;

    if (motion_length >= file_size || gainmap_length > file_size - motion_length ||
        file_size - motion_length - gainmap_length == 0) {
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
    // The MakerNote owner is already resolved by the enclosing ExifIFD.  Do
    // not scan arbitrary bytes for an Apple header: a shadow signature in an
    // unrelated blob is not protocol authority.
    const size_t p = start;
    if (p + 16 > end || std::memcmp(data + p, signature, 10) != 0 || data[p + 10] != 0 ||
        data[p + 11] != 1 || data[p + 12] != 'M' || data[p + 13] != 'M') return false;
    const uint16_t count = read_be16u(data + p + 14);
    if (count == 0 || count > 64 || count > (end - p - 16) / 12) return false;
    const size_t entries = p + 16;
    for (uint16_t i = 0; i < count; ++i) {
        const size_t entry = entries + static_cast<size_t>(i) * 12;
        const uint16_t tag = read_be16u(data + entry);
        if (tag != 0x0011 || read_be16u(data + entry + 2) != 2) continue;
        const uint32_t length = read_be32u(data + entry + 4);
        const uint32_t relative = read_be32u(data + entry + 8);
        if (length == 0 || relative > end - p || length > end - p - relative) {
            out_has_conflict = true;
            return false;
        }
        std::string_view id(reinterpret_cast<const char*>(data + p + relative), length);
        if (!id.empty() && id.back() == '\0') id.remove_suffix(1);
        if (!looks_like_uuid(id)) {
            out_has_conflict = true;
            return false;
        }
        // Duplicate 0x0011 entries are ambiguous even when their text is the
        // same.  There must be one unique, owned ContentIdentifier.
        if (!found_cid.empty()) {
            out_has_conflict = true;
            return false;
        }
        found_cid.assign(id);
    }
    if (!found_cid.empty()) {
        out = found_cid;
        return true;
    }
    return false;
}

static bool jpeg_xmp_structure_invalid(const std::vector<uint8_t>& data,
    std::vector<std::string>* out_packets = nullptr) {
    if (out_packets) out_packets->clear();
    if (data.size() < 2 || data[0] != 0xFF || data[1] != 0xD8) return false;
    constexpr char header[] = "http://ns.adobe.com/xap/1.0/\0";
    constexpr size_t header_size = sizeof(header) - 1;
    constexpr char extension_header[] = "http://ns.adobe.com/xmp/extension/\0";
    constexpr size_t extension_header_size = sizeof(extension_header) - 1;
    struct extension_chunk { std::string guid; uint32_t total{}; uint32_t offset{}; std::vector<uint8_t> bytes; };
    size_t p = 2; size_t packet_count = 0; std::string standard_xml;
    std::vector<extension_chunk> extensions;
    while (p + 2 <= data.size()) {
        if (data[p] != 0xFF) return true;
        while (p < data.size() && data[p] == 0xFF) ++p;
        if (p >= data.size()) return true;
        const uint8_t marker = data[p++];
        if (marker == 0xDA || marker == 0xD9) break;
        if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
        if (p + 2 > data.size()) return true;
        const size_t length = (static_cast<size_t>(data[p]) << 8) | data[p + 1];
        if (length < 2 || length - 2 > data.size() - (p + 2)) return true;
        const size_t payload = p + 2, payload_size = length - 2;
        if (marker == 0xE1 && payload_size >= header_size && std::memcmp(data.data() + payload, header, header_size) == 0) {
            ++packet_count;
            const std::string xml = extract_xml_fragment(std::string_view(reinterpret_cast<const char*>(data.data() + payload + header_size), payload_size - header_size));
            std::vector<xmp_node> nodes;
            if (xml.empty() || !scan_xmp_tree(xml, nodes)) return true;
            if (!standard_xml.empty()) return true;
            standard_xml = xml;
        } else if (marker == 0xE1 && payload_size >= extension_header_size + 32 + 8 &&
                   std::memcmp(data.data() + payload, extension_header, extension_header_size) == 0) {
            const size_t fields = payload + extension_header_size;
            std::string guid(reinterpret_cast<const char*>(data.data() + fields), 32);
            for (char c : guid) if (!std::isxdigit(static_cast<unsigned char>(c))) return true;
            const uint32_t total = (static_cast<uint32_t>(data[fields + 32]) << 24) |
                (static_cast<uint32_t>(data[fields + 33]) << 16) |
                (static_cast<uint32_t>(data[fields + 34]) << 8) | data[fields + 35];
            const uint32_t offset = (static_cast<uint32_t>(data[fields + 36]) << 24) |
                (static_cast<uint32_t>(data[fields + 37]) << 16) |
                (static_cast<uint32_t>(data[fields + 38]) << 8) | data[fields + 39];
            const size_t chunk_start = fields + 40;
            const size_t chunk_size = payload + payload_size - chunk_start;
            if (total == 0 || offset > total || chunk_size > static_cast<size_t>(total - offset)) return true;
            for (const auto& prior : extensions) {
                if (prior.guid == guid && prior.total != total) return true;
                if (prior.guid == guid && prior.offset == offset) return true;
            }
            extensions.push_back({ std::move(guid), total, offset,
                std::vector<uint8_t>(data.begin() + static_cast<std::ptrdiff_t>(chunk_start),
                    data.begin() + static_cast<std::ptrdiff_t>(chunk_start + chunk_size)) });
        }
        p = payload + payload_size;
    }
    if (packet_count > 1) return true;
    if (extensions.empty()) {
        if (out_packets && !standard_xml.empty()) out_packets->push_back(standard_xml);
        return false;
    }
    if (standard_xml.empty() || standard_xml.find(extensions.front().guid) == std::string::npos) return true;
    const std::string& guid = extensions.front().guid;
    const uint32_t total = extensions.front().total;
    for (const auto& chunk : extensions) if (chunk.guid != guid || chunk.total != total) return true;
    std::vector<uint8_t> reconstructed(total, 0);
    std::vector<uint8_t> covered(total, 0);
    for (const auto& chunk : extensions) {
        for (size_t i = 0; i < chunk.bytes.size(); ++i) {
            const size_t at = static_cast<size_t>(chunk.offset) + i;
            if (covered[at] != 0) return true;
            covered[at] = 1;
            reconstructed[at] = chunk.bytes[i];
        }
    }
    for (uint8_t bit : covered) if (bit == 0) return true;
    const std::string extended_xml(reinterpret_cast<const char*>(reconstructed.data()), reconstructed.size());
    std::vector<xmp_node> extended_nodes;
    if (extended_xml.empty() || !scan_xmp_tree(extended_xml, extended_nodes)) return true;
    if (out_packets) {
        out_packets->reserve(2);
        out_packets->push_back(standard_xml);
        out_packets->push_back(extended_xml);
    }
    return false;
}

// Resolve the Apple MakerNote through the formal ExifIFD 0x927C owner. A
// signature search over an arbitrary Exif/HEIF item can otherwise turn an
// unrelated blob into protocol authority.
static bool extract_apple_cid_from_owned_exif(const uint8_t* data, size_t start,
    size_t end, std::string& out, bool& out_has_conflict,
    uint32_t* out_makernote_count = nullptr) {
    out_has_conflict = false;
    if (out_makernote_count) *out_makernote_count = 0;
    if (!data || start >= end || end - start < 8) return false;
    const bool little = data[start] == 'I' && data[start + 1] == 'I';
    const bool big = data[start] == 'M' && data[start + 1] == 'M';
    if (!little && !big) return false;
    auto u16 = [&](size_t p, uint16_t& v) noexcept {
        if (p + 2 > end) return false;
        v = little ? static_cast<uint16_t>(data[p] | (data[p + 1] << 8))
                   : static_cast<uint16_t>((data[p] << 8) | data[p + 1]);
        return true;
    };
    auto u32 = [&](size_t p, uint32_t& v) noexcept {
        if (p + 4 > end) return false;
        v = little ? (static_cast<uint32_t>(data[p]) | (static_cast<uint32_t>(data[p + 1]) << 8) |
                      (static_cast<uint32_t>(data[p + 2]) << 16) | (static_cast<uint32_t>(data[p + 3]) << 24))
                   : ((static_cast<uint32_t>(data[p]) << 24) | (static_cast<uint32_t>(data[p + 1]) << 16) |
                      (static_cast<uint32_t>(data[p + 2]) << 8) | data[p + 3]);
        return true;
    };
    uint16_t magic = 0; uint32_t ifd0 = 0;
    if (!u16(start + 2, magic) || magic != 42 || !u32(start + 4, ifd0) || ifd0 > end - start - 2) return false;

    struct pending_ifd { uint32_t offset; bool formal_exif; };
    std::vector<pending_ifd> pending{{ifd0, false}};
    std::vector<uint32_t> visited;
    bool malformed = false;
    bool formal_found = false;
    uint32_t formal_maker_count = 0;
    uint32_t total_maker_count = 0;
    std::string formal_cid;

    auto looks_like_apple_makernote = [&](size_t value_start, size_t value_length) noexcept {
        return value_length >= 14 && value_start <= end && value_length <= end - value_start &&
            std::memcmp(data + value_start, "Apple iOS\0", 10) == 0 &&
            data[value_start + 10] == 0 && data[value_start + 11] == 1 &&
            data[value_start + 12] == 'M' && data[value_start + 13] == 'M';
    };
    auto resolve_value = [&](size_t entry, uint16_t type, uint32_t count,
        size_t& value_start, size_t& value_length) noexcept {
        if (type != 7 || count == 0) return false;
        value_length = static_cast<size_t>(count);
        if (value_length <= 4) {
            value_start = entry + 8;
            return value_start <= end && value_length <= end - value_start;
        }
        uint32_t offset = 0;
        if (!u32(entry + 8, offset) || offset > end - start || value_length > end - start - offset) return false;
        value_start = start + offset;
        return value_start <= end && value_length <= end - value_start;
    };
    auto load_long_values = [&](size_t entry, uint16_t type, uint32_t count,
        std::vector<uint32_t>& values) noexcept {
        values.clear();
        if (type != 4 || count == 0 || count > 1024 || count > (std::numeric_limits<size_t>::max() / 4)) return false;
        const size_t bytes = static_cast<size_t>(count) * 4;
        size_t value_start = 0;
        if (bytes <= 4) {
            value_start = entry + 8;
        } else {
            uint32_t offset = 0;
            if (!u32(entry + 8, offset) || offset > end - start || bytes > end - start - offset) return false;
            value_start = start + offset;
        }
        if (value_start > end || bytes > end - value_start) return false;
        values.reserve(count);
        for (uint32_t i = 0; i < count; ++i) {
            uint32_t value = 0;
            if (!u32(value_start + static_cast<size_t>(i) * 4, value)) return false;
            values.push_back(value);
        }
        return true;
    };

    while (!pending.empty()) {
        const pending_ifd current = pending.back();
        pending.pop_back();
        if (std::find(visited.begin(), visited.end(), current.offset) != visited.end()) {
            malformed = true;
            continue;
        }
        visited.push_back(current.offset);
        if (current.offset > end - start - 2) { malformed = true; continue; }
        const size_t pos = start + current.offset;
        uint16_t entry_count = 0;
        if (!u16(pos, entry_count) || entry_count > 512 ||
            static_cast<size_t>(entry_count) * 12 > end - pos - 2) {
            malformed = true;
            continue;
        }
        const size_t next_pos = pos + 2 + static_cast<size_t>(entry_count) * 12;
        uint32_t next_ifd = 0;
        if (!u32(next_pos, next_ifd)) { malformed = true; continue; }
        for (uint16_t i = 0; i < entry_count; ++i) {
            const size_t entry = pos + 2 + static_cast<size_t>(i) * 12;
            uint16_t tag = 0, type = 0; uint32_t count = 0, value = 0;
            if (!u16(entry, tag) || !u16(entry + 2, type) || !u32(entry + 4, count) || !u32(entry + 8, value)) {
                malformed = true; break;
            }
            if (tag == 0x927C) {
                ++total_maker_count;
                size_t maker_start = 0, maker_length = 0;
                const bool resolved = resolve_value(entry, type, count, maker_start, maker_length);
                const bool apple_shape = resolved && looks_like_apple_makernote(maker_start, maker_length);
                if (!current.formal_exif) {
                    // A MakerNote in IFD0, IFD1, GPS/Interop/SubIFD, or any
                    // other non-formal owner is never Apple authority.  Only
                    // flag it as an Apple ambiguity when its owned bytes have
                    // Apple's protocol signature, so unrelated vendor EXIF
                    // remains eligible for its own protocol detector.
                    if (apple_shape) out_has_conflict = true;
                    continue;
                }
                if (!resolved) {
                    out_has_conflict = apple_shape;
                    malformed = true;
                    continue;
                }
                ++formal_maker_count;
                if (formal_maker_count > 1 || formal_found) {
                    out_has_conflict = true;
                    continue;
                }
                std::string candidate;
                bool candidate_conflict = false;
                if (!extract_apple_cid_from_makernote(data, maker_start,
                        maker_start + maker_length, candidate, candidate_conflict)) {
                    if (apple_shape || candidate_conflict) out_has_conflict = true;
                    continue;
                }
                if (candidate_conflict) { out_has_conflict = true; continue; }
                formal_found = true;
                formal_cid = std::move(candidate);
                continue;
            }
            if (tag == 0x8769) {
                std::vector<uint32_t> values;
                if (!load_long_values(entry, type, count, values) || values.size() != 1) {
                    malformed = true;
                } else {
                    pending.push_back({values[0], current.offset == ifd0});
                }
            } else if (tag == 0x8825 || tag == 0xA005) {
                std::vector<uint32_t> values;
                if (!load_long_values(entry, type, count, values) || values.size() != 1) malformed = true;
                else pending.push_back({values[0], false});
            } else if (tag == 0x014A) {
                std::vector<uint32_t> values;
                if (!load_long_values(entry, type, count, values)) malformed = true;
                else for (uint32_t value_offset : values) pending.push_back({value_offset, false});
            }
        }
        if (next_ifd != 0) pending.push_back({next_ifd, false});
    }
    if (out_makernote_count) *out_makernote_count = total_maker_count;
    // Once a formal Apple ContentIdentifier exists, every other MakerNote
    // owner is a structural shadow regardless of its payload bytes.  Payload
    // signatures are only used to reject an Apple-shaped unowned tag when no
    // formal owner exists; they never make an extra owner benign.
    if (formal_found && total_maker_count != 1) out_has_conflict = true;
    if (malformed || out_has_conflict || !formal_found) return false;
    out = std::move(formal_cid);
    return true;
}

static bool extract_apple_cid_from_image(lpb_context* context, const std::vector<uint8_t>& data,
    lpb_image_container container, std::string& out, bool& out_has_conflict) {
    out_has_conflict = false;
    if (container == LPB_IMAGE_CONTAINER_JPEG && data.size() >= 2 && data[0] == 0xFF && data[1] == 0xD8) {
        size_t p = 2;
        std::string found_cid;
        uint32_t total_makernote_count = 0;
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
                std::string candidate;
                bool candidate_conflict = false;
                uint32_t candidate_makernote_count = 0;
                if (extract_apple_cid_from_owned_exif(data.data(), payload + 6,
                        payload + payload_size, candidate, candidate_conflict,
                        &candidate_makernote_count)) {
                    if (!found_cid.empty()) {
                        out_has_conflict = true;
                        return false;
                    }
                    found_cid = std::move(candidate);
                }
                if (candidate_makernote_count > std::numeric_limits<uint32_t>::max() - total_makernote_count) {
                    out_has_conflict = true;
                    return false;
                }
                total_makernote_count += candidate_makernote_count;
                if (candidate_conflict) out_has_conflict = true;
                if (out_has_conflict) return false;
            }
            p = payload + payload_size;
        }
        if (!found_cid.empty() && total_makernote_count != 1) {
            out_has_conflict = true;
            return false;
        }
        if (!found_cid.empty()) {
            out = std::move(found_cid);
            return true;
        }
        return false;
    }
    if (container == LPB_IMAGE_CONTAINER_HEIC) {
        uint64_t offset = 0, length = 0;
        if (lpb_heif_locate_exif_item(context, data.data(), data.size(), &offset, &length) != LPB_RESULT_OK ||
            offset > data.size() || length > data.size() - static_cast<size_t>(offset)) return false;
        size_t owned_start = static_cast<size_t>(offset);
        size_t owned_end = static_cast<size_t>(offset + length);
        if (owned_end - owned_start >= 10 && data[owned_start] == 0 && data[owned_start + 1] == 0 &&
            data[owned_start + 2] == 0 && data[owned_start + 3] == 6 &&
            std::memcmp(data.data() + owned_start + 4, "Exif\0\0", 6) == 0)
            owned_start += 10;
        else if (owned_end - owned_start >= 6 && std::memcmp(data.data() + owned_start, "Exif\0\0", 6) == 0)
            owned_start += 6;
        return extract_apple_cid_from_owned_exif(data.data(), owned_start, owned_end, out, out_has_conflict);
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

static bool is_valid_mov_container(const uint8_t* data, size_t size) noexcept;

static bool is_apple_mov_structural_container(std::string_view type) noexcept {
    return type == "moov" || type == "trak" || type == "mdia" || type == "minf" ||
        type == "stbl" || type == "udta" || type == "edts" || type == "dinf" ||
        type == "meta" || type == "ilst";
}

static bool apple_keys_box_contains_content_identifier(
    const uint8_t* data,
    const isobmff_box_header& keys,
    size_t keys_start,
    bool& out_contains) noexcept {
    out_contains = false;
    if (keys.size < keys.header_size + 8) return false;
    const size_t keys_end = keys_start + keys.size;
    const size_t body = keys_start + keys.header_size;
    if (read_be32u(data + body) != 0) return false;
    const uint32_t count = read_be32u(data + body + 4);
    if (count == 0 || count > 1024) return false;
    size_t p = body + 8;
    constexpr std::string_view content_key = "com.apple.quicktime.content.identifier";
    for (uint32_t index = 0; index < count; ++index) {
        if (p > keys_end || keys_end - p < 8) return false;
        const uint32_t size = read_be32u(data + p);
        if (size < 8 || size > keys_end - p) return false;
        const size_t name_size = size - 8;
        if (std::memcmp(data + p + 4, "mdta", 4) == 0 && name_size == content_key.size() &&
            std::memcmp(data + p + 8, content_key.data(), name_size) == 0) {
            out_contains = true;
        }
        p += size;
    }
    return p == keys_end;
}

// Walk only boxes whose payload is a legal child-box region.  This deliberately
// does not scan opaque media/sample payloads for FourCC bytes.  The Apple CID
// authority is moov/meta/{hdlr,keys,ilst}; any second metadata hierarchy or
// metadata child found on another parsed path is a shadow, not a fallback.
static bool reject_apple_mov_shadow_hierarchy(
    lpb_context* context,
    const uint8_t* data,
    size_t start,
    size_t end,
    size_t authoritative_meta_start,
    bool inside_authoritative_meta,
    bool inside_track_meta,
    bool inside_udta,
    size_t metadata_depth,
    std::string_view parent_type) noexcept {
    size_t p = start;
    while (p < end) {
        isobmff_box_header box{};
        if (!try_read_box_header(data, p, end, box)) {
            set_error(context, "Apple MOV shadow hierarchy contains a malformed box.");
            return false;
        }
        const std::string_view type(reinterpret_cast<const char*>(data + p + 4), 4);

        const bool enters_track_meta = type == "meta" && p != authoritative_meta_start && parent_type == "trak";
        if (type == "meta") {
            if (p != authoritative_meta_start && !enters_track_meta) {
                set_error(context, "Apple MOV contains a nested or shadow metadata hierarchy.");
                return false;
            }
        }
        if (type == "keys" || type == "ilst") {
            if (inside_track_meta && metadata_depth != 1) {
                set_error(context, "Apple MOV track metadata contains a nested keys/ilst hierarchy.");
                return false;
            }
            if (!inside_authoritative_meta && !inside_track_meta) {
                set_error(context, "Apple MOV contains a shadow keys/ilst hierarchy.");
                return false;
            }
        }
        if (type == "keys" && inside_track_meta) {
            bool contains_content_identifier = false;
            if (!apple_keys_box_contains_content_identifier(data, box, p, contains_content_identifier)) {
                set_error(context, "Apple MOV track metadata keys box is malformed.");
                return false;
            }
            if (contains_content_identifier) {
                set_error(context, "Apple MOV track metadata shadows the ContentIdentifier authority.");
                return false;
            }
        }
        // `data` is also a normal payload box in several media/sample
        // hierarchies.  It is only a competing metadata declaration when it
        // is owned by an udta metadata container; do not reject a legitimate
        // data token merely because it occurs below a parsed media box.
        if (type == "data" && inside_udta && !inside_authoritative_meta && !inside_track_meta) {
            set_error(context, "Apple MOV contains a shadow metadata data box.");
            return false;
        }
        // hdlr is also legal in ordinary media tracks, so only enforce its
        // ownership when it is encountered below the authoritative meta.
        if (type == "hdlr" && (inside_authoritative_meta || inside_track_meta) && metadata_depth != 1) {
            set_error(context, "Apple MOV metadata contains a shadow hdlr hierarchy.");
            return false;
        }
        if (type == "hdlr" && (inside_authoritative_meta || inside_track_meta)) {
            const size_t hdlr_body = p + box.header_size;
            const bool mdta_layout = box.size >= box.header_size + 24 &&
                std::memcmp(data + hdlr_body + 8, "mdta", 4) == 0 &&
                read_be32u(data + hdlr_body + 12) == 0 &&
                read_be32u(data + hdlr_body + 16) == 0 &&
                read_be32u(data + hdlr_body + 20) == 0;
            const bool mdir_layout = box.size >= box.header_size + 24 &&
                std::memcmp(data + hdlr_body + 8, "mdir", 4) == 0 &&
                std::memcmp(data + hdlr_body + 12, "appl", 4) == 0 &&
                read_be32u(data + hdlr_body + 16) == 0 &&
                read_be32u(data + hdlr_body + 20) == 0;
            if (box.size < box.header_size + 24 ||
                read_be32u(data + p + box.header_size) != 0 ||
                read_be32u(data + p + box.header_size + 4) != 0 ||
                (!mdta_layout && !mdir_layout)) {
                set_error(context, "Apple MOV metadata hdlr FullBox or reserved fields are malformed.");
                return false;
            }
        }

        const bool item_child_region = parent_type == "ilst";
        const bool child_region = item_child_region || is_apple_mov_structural_container(type);
        if (child_region && type != "keys" && type != "hdlr" && type != "data") {
            const bool enters_authoritative_meta = type == "meta" && p == authoritative_meta_start;
            const size_t child_depth = (enters_authoritative_meta || enters_track_meta)
                ? 1 : metadata_depth + ((inside_authoritative_meta || inside_track_meta) ? 1 : 0);
            if (!reject_apple_mov_shadow_hierarchy(
                    context, data, p + box.header_size, p + box.size,
                    authoritative_meta_start,
                    inside_authoritative_meta || enters_authoritative_meta,
                    inside_track_meta || enters_track_meta,
                    inside_udta || type == "udta",
                    child_depth, type)) {
                return false;
            }
        }
        p += box.size;
    }
    return p == end;
}

static bool extract_apple_cid_from_video(lpb_context* context, const std::vector<uint8_t>& data, std::string& out) {
    size_t moov_start = 0;
    isobmff_box_header moov{};
    size_t p = 0;
    while (p < data.size()) {
        isobmff_box_header box{};
        if (!try_read_box_header(data.data(), p, data.size(), box)) { set_error(context, "Apple MOV top-level box is malformed."); return false; }
        if (std::memcmp(data.data() + p + 4, "moov", 4) == 0) {
            if (moov.size != 0) { set_error(context, "Apple MOV contains duplicate moov boxes."); return false; }
            moov_start = p;
            moov = box;
        }
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
        if (std::memcmp(data.data() + p + 4, "meta", 4) == 0) {
            if (meta.size != 0) { set_error(context, "Apple MOV contains duplicate metadata boxes."); return false; }
            meta_start = p;
            meta = box;
        }
        p += box.size;
    }
    if (meta.size == 0) { set_error(context, "Apple MOV metadata box was not found."); return false; }
    const size_t meta_end = meta_start + meta.size;
    // Apple's QuickTime mdta `meta` is a plain container (no full-box
    // version/flags bytes); its children begin immediately after the box
    // header.  Treating those bytes as an optional prefix would allow a
    // shadow hierarchy to be selected by first-match behavior.
    const size_t child_start = meta_start + meta.header_size;
    if (child_start > meta_end) { set_error(context, "Apple MOV metadata hierarchy is out of bounds."); return false; }

    size_t keys_start = 0, ilst_start = 0, hdlr_start = 0;
    isobmff_box_header keys{}, ilst{}, hdlr{};
    p = child_start;
    while (p < meta_end) {
        isobmff_box_header box{};
        if (!try_read_box_header(data.data(), p, meta_end, box)) { set_error(context, "Apple MOV metadata child is malformed."); return false; }
        if (std::memcmp(data.data() + p + 4, "keys", 4) == 0) {
            if (keys.size != 0) { set_error(context, "Apple MOV metadata contains duplicate keys boxes."); return false; }
            keys_start = p;
            keys = box;
        }
        if (std::memcmp(data.data() + p + 4, "ilst", 4) == 0) {
            if (ilst.size != 0) { set_error(context, "Apple MOV metadata contains duplicate ilst boxes."); return false; }
            ilst_start = p;
            ilst = box;
        }
        if (std::memcmp(data.data() + p + 4, "hdlr", 4) == 0) {
            if (hdlr.size != 0) { set_error(context, "Apple MOV metadata contains duplicate hdlr boxes."); return false; }
            hdlr_start = p;
            hdlr = box;
        }
        p += box.size;
    }
    if (keys.size == 0 || ilst.size == 0 || hdlr.size == 0 || keys.size < keys.header_size + 8) {
        set_error(context, "Apple MOV mdta hdlr/keys/ilst boxes were not found.");
        return false;
    }
    // QuickTime uses an `mdir` handler owned by Apple (`appl`) for the
    // metadata hierarchy found in MOV fixtures, while the documented ISO
    // layout uses an `mdta` handler with a zero manufacturer.  Both are
    // formal handler layouts; accepting either does not relax ownership
    // because the component/manufacturer pair is checked as one identity.
    const size_t hdlr_body = hdlr_start + hdlr.header_size;
    const bool has_valid_handler_layout = hdlr.size >= hdlr.header_size + 24 &&
        read_be32u(data.data() + hdlr_body) == 0 &&
        read_be32u(data.data() + hdlr_body + 4) == 0 &&
        ((std::memcmp(data.data() + hdlr_body + 8, "mdta", 4) == 0 &&
            read_be32u(data.data() + hdlr_body + 12) == 0) ||
         (std::memcmp(data.data() + hdlr_body + 8, "mdir", 4) == 0 &&
            std::memcmp(data.data() + hdlr_body + 12, "appl", 4) == 0)) &&
        read_be32u(data.data() + hdlr_body + 16) == 0 &&
        read_be32u(data.data() + hdlr_body + 20) == 0;
    if (!has_valid_handler_layout) {
        set_error(context, "Apple MOV metadata hdlr does not own a supported mdta hierarchy.");
        return false;
    }
    if (!reject_apple_mov_shadow_hierarchy(
            context, data.data(), moov_start + moov.header_size, moov_end,
            meta_start, false, false, false, 0, "moov")) {
        return false;
    }

    const size_t keys_body = keys_start + keys.header_size;
    if (read_be32u(data.data() + keys_body) != 0) {
        set_error(context, "Apple MOV mdta keys FullBox version or flags are malformed.");
        return false;
    }
    const uint32_t key_count = read_be32u(data.data() + keys_body + 4);
    if (key_count == 0 || key_count > 1024) return false;
    size_t key_pos = keys_body + 8;
    uint32_t content_key_index = 0;
    std::vector<std::string_view> key_names;
    for (uint32_t index = 1; index <= key_count; ++index) {
        if (key_pos > keys_start + keys.size || keys_start + keys.size - key_pos < 8) return false;
        const uint32_t key_size = read_be32u(data.data() + key_pos);
        if (key_size < 8 || key_size > keys_start + keys.size - key_pos) return false;
        if (std::memcmp(data.data() + key_pos + 4, "mdta", 4) != 0) {
            set_error(context, "Apple MOV mdta keys contains a key with an unsupported namespace.");
            return false;
        }
        const std::string_view key_name(reinterpret_cast<const char*>(data.data() + key_pos + 8), key_size - 8);
        if (std::find(key_names.begin(), key_names.end(), key_name) != key_names.end()) {
            set_error(context, "Apple MOV mdta keys contains duplicate key names.");
            return false;
        }
        key_names.push_back(key_name);
        if (std::memcmp(data.data() + key_pos + 4, "mdta", 4) == 0 &&
            key_size - 8 == std::strlen("com.apple.quicktime.content.identifier") &&
            std::memcmp(data.data() + key_pos + 8, "com.apple.quicktime.content.identifier", key_size - 8) == 0) {
            if (content_key_index != 0) {
                set_error(context, "Apple MOV mdta keys contains duplicate content identifier keys.");
                return false;
            }
            content_key_index = index;
        }
        key_pos += key_size;
    }
    if (content_key_index == 0 || key_pos != keys_start + keys.size) { set_error(context, "Apple MOV content identifier key was not found."); return false; }

    p = ilst_start + ilst.header_size;
    const size_t ilst_end = ilst_start + ilst.size;
    uint32_t item_count_seen = 0;
    std::vector<uint32_t> item_indices;
    std::string found_id;
    while (p < ilst_end) {
        isobmff_box_header item{};
        if (!try_read_box_header(data.data(), p, ilst_end, item) || item.size < item.header_size + 8) { set_error(context, "Apple MOV ilst entry is malformed."); return false; }
        // ilst item type is the 32-bit mdta key index, not a FourCC.
        const uint32_t item_index = read_be32u(data.data() + p + 4);
        if (item_index == 0 || item_index > key_count ||
            std::find(item_indices.begin(), item_indices.end(), item_index) != item_indices.end()) {
            set_error(context, "Apple MOV ilst contains a duplicate or out-of-range key index.");
            return false;
        }
        item_indices.push_back(item_index);
        ++item_count_seen;
        if (item_index == content_key_index) {
            if (!found_id.empty()) {
                set_error(context, "Apple MOV ilst contains duplicate content identifier items.");
                return false;
            }
            const size_t data_pos = p + item.header_size;
            isobmff_box_header value_box{};
            if (!try_read_box_header(data.data(), data_pos, p + item.size, value_box) ||
                std::memcmp(data.data() + data_pos + 4, "data", 4) != 0 ||
                value_box.size < value_box.header_size + 8 ||
                data_pos + value_box.size != p + item.size) {
                set_error(context, "Apple MOV content identifier data box is malformed or shadowed.");
                return false;
            }
            // QuickTime `data` stores version/flags followed by the UTF-8
            // value type (1) and locale.  A different type is not an
            // equivalent ContentIdentifier representation.
            if (read_be32u(data.data() + data_pos + value_box.header_size) != 1) {
                set_error(context, "Apple MOV content identifier data value is not UTF-8.");
                return false;
            }
            const size_t value_start = data_pos + value_box.header_size + 8;
            const size_t value_end = data_pos + value_box.size;
            std::string_view value(reinterpret_cast<const char*>(data.data() + value_start), value_end - value_start);
            while (!value.empty() && value.back() == '\0') value.remove_suffix(1);
            if (!looks_like_uuid(value)) { set_error(context, "Apple MOV content identifier value is not a UUID."); return false; }
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

static bool is_valid_heic_container(lpb_context* context, const uint8_t* data, size_t size) noexcept {
    (void)context;
    if (!data || size < 12) return false;
    size_t pos = 0;
    bool saw_ftyp = false;
    while (pos < size) {
        isobmff_box_header box{};
        if (!try_read_box_header(data, pos, size, box)) return false;
        if (pos == 0) {
            if (std::memcmp(data + pos + 4, "ftyp", 4) != 0) return false;
            saw_ftyp = true;
        }
        pos += box.size;
    }
    if (!saw_ftyp || pos != size) return false;

    // The authoritative auxiliary graph is enumerated later when facts are
    // published.  The primary container gate remains structural here so
    // metadata-only HEIFs without an auxiliary relation remain readable.
    return true;
}

// Google Motion Photo V2 HEIC stores the motion resource as the payload of a
// top-level mpvd box.  The box header is alignment/presentation framing and is
// not part of the declared video item length.  Keep this resolution entirely
// hierarchy-aware; no JPEG EOI or byte-tail signature is involved.
static bool locate_google_heic_motion_payload(
    const std::vector<uint8_t>& data,
    size_t& out_primary_end,
    size_t& out_video_offset,
    size_t& out_video_length,
    isobmff_box_header& out_mpvd) noexcept {
    out_primary_end = 0;
    out_video_offset = 0;
    out_video_length = 0;
    out_mpvd = {};
    if (data.empty()) return false;

    size_t pos = 0;
    bool saw_ftyp = false;
    bool saw_meta = false;
    bool saw_mpvd = false;
    while (pos < data.size()) {
        isobmff_box_header box{};
        if (!try_read_box_header(data.data(), pos, data.size(), box)) return false;
        const bool is_ftyp = std::memcmp(data.data() + pos + 4, "ftyp", 4) == 0;
        const bool is_meta = std::memcmp(data.data() + pos + 4, "meta", 4) == 0;
        const bool is_mpvd = std::memcmp(data.data() + pos + 4, "mpvd", 4) == 0;
        if (pos == 0) {
            if (!is_ftyp) return false;
            saw_ftyp = true;
        } else if (is_ftyp) {
            return false;
        }
        if (is_meta) {
            if (saw_meta) return false;
            saw_meta = true;
        }
        if (is_mpvd) {
            if (saw_mpvd || box.size <= box.header_size) return false;
            if (pos + box.size != data.size()) return false;
            saw_mpvd = true;
            out_primary_end = pos;
            out_video_offset = pos + box.header_size;
            out_video_length = box.size - box.header_size;
            out_mpvd = box;
        }
        pos += box.size;
    }
    return saw_ftyp && saw_meta && saw_mpvd && out_primary_end > 0 &&
        out_video_offset <= data.size() && out_video_length <= data.size() - out_video_offset;
}

static bool is_valid_mov_container(const uint8_t* data, size_t size) noexcept {
    // MOV may legally omit ftyp, but still needs an actual video track and
    // sample tables.  The validator is hierarchy-aware and does not accept
    // arbitrary box-type byte hits.
    return is_valid_mov_media_range(data, size, 0, size);
}

static bool contains_text(std::span<const uint8_t> data, std::string_view value)
{
    if (value.empty() || data.size() < value.size()) return false;
    const auto* begin = data.data();
    const auto* end = begin + data.size();
    return std::search(begin, end, value.begin(), value.end()) != end;
}

static bool contains_text_in_moov(std::span<const uint8_t> data, std::string_view value)
{
    if (value.empty()) return false;
    const size_t moov = find_top_level_box(data, "moov");
    if (moov == std::numeric_limits<size_t>::max() || moov + 8 > data.size()) return false;
    const uint32_t moov_size = read_be32(data.data() + moov);
    if (moov_size < 8 || moov_size > data.size() - moov) return false;
    const auto* begin = data.data() + moov;
    const auto* end = begin + moov_size;
    return std::search(begin, end, value.begin(), value.end()) != end;
}


static bool apple_image_has_tag(lpb_context* context, const std::vector<uint8_t>& data,
    lpb_image_container container, uint16_t tag) {
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
                if (lpb::protocols::apple::apple_makernote_has_tag(data.data(), payload + 6, payload + payload_size, tag)) {
                    return true;
                }
            }
            p = payload + payload_size;
        }
        return false;
    }
    if (container == LPB_IMAGE_CONTAINER_HEIC) {
        uint64_t offset = 0, length = 0;
        if (lpb_heif_locate_exif_item(context, data.data(), data.size(), &offset, &length) != LPB_RESULT_OK ||
            offset > data.size() || length > data.size() - static_cast<size_t>(offset)) return false;
        return lpb::protocols::apple::apple_makernote_has_tag(data.data(), static_cast<size_t>(offset),
            static_cast<size_t>(offset + length), tag);
    }
    return false;
}

static void add_residue(
    std::vector<lpb_confirmed_residue>* out_residues,
    const char* id,
    lpb_source_protocol owner_protocol,
    lpb_media_artifact_kind artifact_role,
    lpb_residue_structure_kind structure_kind,
    const char* selector,
    const char* expected_semantic = "",
    const char* expected_fingerprint = "",
    lpb_coordinate_space coordinate_space = LPB_COORD_STRUCTURED_SELECTOR,
    lpb_residue_removal_mode removal_mode = LPB_REMOVAL_DELETE,
    int32_t required_after_extraction = 1)
{
    if (!out_residues) return;
    lpb_confirmed_residue r{};
    r.struct_size = sizeof(lpb_confirmed_residue);
    strncpy_s(r.residue_id, id, _TRUNCATE);
    r.owner_protocol = owner_protocol;
    r.artifact_role = artifact_role;
    r.structure_kind = structure_kind;
    strncpy_s(r.selector, selector, _TRUNCATE);
    if (expected_semantic) strncpy_s(r.expected_semantic, expected_semantic, _TRUNCATE);
    if (expected_fingerprint) strncpy_s(r.expected_fingerprint, expected_fingerprint, _TRUNCATE);
    r.coordinate_space = coordinate_space;
    r.removal_mode = removal_mode;
    r.required_after_extraction = required_after_extraction;
    out_residues->push_back(r);
}

static bool publish_auxiliary_item(
    lpb_source_media_facts* facts,
    const std::vector<uint8_t>& source_data,
    lpb_image_container container,
    lpb_media_range range,
    lpb_auxiliary_representation representation,
    lpb_auxiliary_ownership ownership,
    uint32_t item_id,
    const char* relationship,
    const char* semantic,
    const char* stable_identity,
    const char* owner_identity,
    lpb_auxiliary_codec codec = LPB_AUX_CODEC_UNKNOWN,
    int32_t source_index = 0) noexcept {
    if (!facts || facts->auxiliary_count >= 8 || range.length == 0 || !relationship ||
        !semantic || !stable_identity || !owner_identity ||
        range.offset > source_data.size() || range.length > source_data.size() - static_cast<size_t>(range.offset)) return false;
    const uint32_t auxiliary_index = facts->auxiliary_count;
    auto& item = facts->auxiliary_items[auxiliary_index];
    item.struct_size = sizeof(lpb_auxiliary_item_facts);
    item.is_present = 1;
    item.container = container;
    item.representation = representation;
    item.ownership = ownership;
    item.item_id = item_id == 0 ? auxiliary_index + 1 : item_id;
    item.file_range = range;
    strncpy_s(item.relationship, relationship, _TRUNCATE);
    item.codec = codec;
    item.source_index = source_index;
    lpb::crypto::sha256_buffer(source_data.data() + static_cast<size_t>(range.offset),
        static_cast<size_t>(range.length), item.sha256);
    strncpy_s(item.stable_identity, stable_identity, _TRUNCATE);
    strncpy_s(item.owner_identity, owner_identity, _TRUNCATE);
    strncpy_s(item.semantic, semantic, _TRUNCATE);
    ++facts->auxiliary_count;

    return true;
}

static bool publish_gainmap_auxiliary(lpb_source_media_facts* facts,
    const std::vector<uint8_t>& source_data,
    lpb_image_container container, lpb_media_range range,
    lpb_auxiliary_representation representation = LPB_AUX_REPRESENTATION_EMBEDDED,
    lpb_auxiliary_ownership ownership = LPB_AUX_OWNER_PRIMARY,
    uint32_t item_id = 0,
    const char* relationship = "gain-map",
    const char* stable_identity = nullptr) noexcept {
    const uint32_t next_index = facts ? facts->auxiliary_count : 0;
    const uint32_t auxiliary_index = next_index;
    std::string generated_identity = "auxiliary:gainmap:" + std::to_string(next_index);
    if (!publish_auxiliary_item(
            facts, source_data, container, range, representation, ownership, item_id,
            relationship, "GainMap", stable_identity ? stable_identity : generated_identity.c_str(),
            ownership == LPB_AUX_OWNER_PRIMARY ? "primary:0" : "auxiliary:0",
            container == LPB_IMAGE_CONTAINER_JPEG ? LPB_AUX_CODEC_JPEG : LPB_AUX_CODEC_HEVC)) {
        return false;
    }

    auto& gain_map = facts->gain_map;
    gain_map.struct_size = sizeof(lpb_gainmap_item_facts);
    gain_map.is_present = 1;
    gain_map.container = container;
    gain_map.representation = static_cast<int32_t>(representation);
    gain_map.ownership = static_cast<int32_t>(ownership);
    gain_map.owner_artifact_role = ownership == LPB_AUX_OWNER_PRIMARY
        ? LPB_ARTIFACT_PRIMARY_IMAGE : LPB_ARTIFACT_AUXILIARY_ITEM;
    gain_map.auxiliary_index = auxiliary_index;
    gain_map.item_id = facts->auxiliary_items[auxiliary_index].item_id;
    gain_map.file_range = range;
    strncpy_s(gain_map.relationship, relationship, _TRUNCATE);
    return true;
}

static bool publish_preservation_carrier(
    lpb_source_media_facts* facts,
    const std::vector<uint8_t>& source_data,
    lpb_preservation_carrier_kind kind,
    lpb_media_range range,
    const char* stable_identity,
    const char* semantic,
    const char* owner_identity,
    const char* relationship,
    int32_t source_index = 0,
    lpb_image_container container = LPB_IMAGE_CONTAINER_JPEG,
    lpb_auxiliary_codec codec = LPB_AUX_CODEC_UNKNOWN,
    lpb_media_artifact_kind artifact_role = LPB_ARTIFACT_PRIMARY_IMAGE) noexcept {
    if (!facts || facts->preservation_carrier_count >= 8 || range.length == 0 ||
        !stable_identity || !semantic || !owner_identity || !relationship ||
        range.offset > source_data.size() || range.length > source_data.size() - static_cast<size_t>(range.offset)) {
        return false;
    }
    auto& carrier = facts->preservation_carriers[facts->preservation_carrier_count];
    carrier = {};
    carrier.struct_size = sizeof(lpb_preservation_carrier_facts);
    carrier.is_present = 1;
    carrier.kind = kind;
    carrier.source_index = source_index;
    carrier.artifact_role = artifact_role;
    carrier.container = container;
    carrier.codec = codec;
    carrier.file_range = range;
    lpb::crypto::sha256_buffer(source_data.data() + static_cast<size_t>(range.offset),
        static_cast<size_t>(range.length), carrier.sha256);
    strncpy_s(carrier.stable_identity, stable_identity, _TRUNCATE);
    strncpy_s(carrier.semantic, semantic, _TRUNCATE);
    strncpy_s(carrier.owner_identity, owner_identity, _TRUNCATE);
    strncpy_s(carrier.relationship, relationship, _TRUNCATE);
    ++facts->preservation_carrier_count;
    return true;
}

struct samsung_sef_preservation_entry {
    uint16_t marker{0};
    size_t payload_offset{0};
    size_t payload_length{0};
    std::string name;
};

// Parse the complete Samsung directory and return only non-MotionPhoto
// entries.  The directory/index itself is returned separately as a carrier;
// the motion entries are source-protocol residue and are deliberately not
// represented as preservation data.
static bool collect_samsung_sef_preservation_entries(
    const std::vector<uint8_t>& data,
    uint64_t trailer_offset,
    bool reject_motion_entries,
    size_t& out_sefh,
    std::vector<samsung_sef_preservation_entry>& out_entries) noexcept {
    out_sefh = 0;
    out_entries.clear();
    if (trailer_offset > data.size() || data.size() - static_cast<size_t>(trailer_offset) < 16) return false;

    const size_t start = static_cast<size_t>(trailer_offset);
    const size_t end = data.size();
    const size_t footer = end - 8;
    if (std::memcmp(data.data() + footer + 4, "SEFT", 4) != 0) return false;

    const auto read_le16 = [&](size_t at) noexcept -> uint16_t {
        return static_cast<uint16_t>(data[at]) | (static_cast<uint16_t>(data[at + 1]) << 8);
    };
    const auto read_le32 = [&](size_t at) noexcept -> uint32_t {
        return static_cast<uint32_t>(data[at]) |
            (static_cast<uint32_t>(data[at + 1]) << 8) |
            (static_cast<uint32_t>(data[at + 2]) << 16) |
            (static_cast<uint32_t>(data[at + 3]) << 24);
    };

    const uint32_t total_size = read_le32(footer);
    const size_t trailer_length = end - start;
    if (total_size < 12 || static_cast<uint64_t>(total_size) > trailer_length - 8) return false;
    const size_t sefh = footer - static_cast<size_t>(total_size);
    if (sefh < start || sefh + 12 > footer || std::memcmp(data.data() + sefh, "SEFH", 4) != 0) return false;

    const uint32_t entry_count = read_le32(sefh + 8);
    if (entry_count > (footer - (sefh + 12)) / 12 ||
        sefh + 12 + static_cast<size_t>(entry_count) * 12 != footer) return false;

    std::vector<std::pair<size_t, size_t>> payload_ranges;
    std::vector<std::string> identities;
    payload_ranges.reserve(entry_count);
    out_entries.reserve(entry_count);
    identities.reserve(entry_count);

    for (uint32_t i = 0; i < entry_count; ++i) {
        const size_t directory_entry = sefh + 12 + static_cast<size_t>(i) * 12;
        const uint16_t prefix = read_le16(directory_entry);
        const uint16_t marker = read_le16(directory_entry + 2);
        const uint32_t back_offset = read_le32(directory_entry + 4);
        const uint32_t payload_size = read_le32(directory_entry + 8);
        if (payload_size < 8 || static_cast<uint64_t>(back_offset) > static_cast<uint64_t>(sefh - start) ||
            payload_size > back_offset) return false;

        const size_t payload = sefh - static_cast<size_t>(back_offset);
        if (payload < start || payload > sefh - static_cast<size_t>(payload_size)) return false;
        const size_t payload_end = payload + static_cast<size_t>(payload_size);
        if (read_le16(payload) != prefix || read_le16(payload + 2) != marker) return false;
        for (const auto& prior : payload_ranges) {
            if (payload < prior.second && prior.first < payload_end) return false;
        }
        payload_ranges.emplace_back(payload, payload_end);

        const uint32_t name_size = read_le32(payload + 4);
        if (name_size > payload_size - 8) return false;
        std::string name(reinterpret_cast<const char*>(data.data() + payload + 8), name_size);

        const bool is_motion_data = marker == 0x0A30;
        const bool is_motion_version = marker == 0x0A31;
        if (is_motion_data || is_motion_version) {
            if (reject_motion_entries) return false;
            if (is_motion_data) {
                if (prefix != 0 || name_size != 16 || payload_size < 24 || name != "MotionPhoto_Data" ||
                    !is_valid_isobmff_media_range(data.data(), data.size(), payload + 24, payload_size - 24)) {
                    return false;
                }
            } else if (prefix != 0 || name_size != 19 || payload_size < 31 || name != "MotionPhoto_Version") {
                return false;
            }
            continue;
        }

        const std::string identity = "samsung:sef:entry:" + std::to_string(marker) + ":" + name;
        if (identity.size() >= 96 || name.size() >= 96 ||
            std::find(identities.begin(), identities.end(), identity) != identities.end()) return false;
        identities.push_back(identity);
        out_entries.push_back(samsung_sef_preservation_entry{ marker, payload, payload_size, std::move(name) });
    }

    out_sefh = sefh;
    return true;
}

static bool publish_samsung_sef_preservation_carriers(
    lpb_source_media_facts* facts,
    const std::vector<uint8_t>& source_data,
    uint64_t trailer_offset,
    bool reject_motion_entries) noexcept {
    size_t sefh = 0;
    std::vector<samsung_sef_preservation_entry> entries;
    if (!collect_samsung_sef_preservation_entries(
            source_data, trailer_offset, reject_motion_entries, sefh, entries) ||
        entries.size() + 1 > 8) {
        return false;
    }

    for (const auto& entry : entries) {
        const std::string identity = "samsung:sef:entry:" + std::to_string(entry.marker) + ":" + entry.name;
        const std::string relationship = "SEFH/SEFT:" + entry.name;
        if (relationship.size() >= 96 ||
            !publish_preservation_carrier(
                facts, source_data, LPB_PRESERVATION_CARRIER_SAMSUNG_SEF,
                { entry.payload_offset, entry.payload_length }, identity.c_str(), entry.name.c_str(),
                "primary:0", relationship.c_str(), 0, LPB_IMAGE_CONTAINER_JPEG,
                LPB_AUX_CODEC_UNKNOWN, LPB_ARTIFACT_PRIMARY_IMAGE)) {
            return false;
        }
    }

    return publish_preservation_carrier(
        facts, source_data, LPB_PRESERVATION_CARRIER_SAMSUNG_SEF,
        { sefh, source_data.size() - sefh }, "samsung:sef:index-trailer", "SamsungSEFIndexTrailer",
        "primary:0", "SEFH/SEFT", 0, LPB_IMAGE_CONTAINER_JPEG, LPB_AUX_CODEC_UNKNOWN,
        LPB_ARTIFACT_PRIMARY_IMAGE);
}

// Samsung JPEG keeps the Google Motion Photo directory in XMP, but its
// private SEF trailer places the MotionPhoto_Data payload after additional
// trailer entries.  Therefore the GainMap cannot be located by subtracting
// all Item:Length values from the SEF video offset.  The formal ownership
// relation for this layout is:
//
//   JPEG EOI | GainMap(Item:Length) | Primary:Padding | SEF video
//
// The SEF parser supplies the authoritative video range; this helper only
// publishes a GainMap when the XMP directory and both independent ranges agree
// exactly.  0 = no declared GainMap, 1 = published, -1 = malformed/shadowed
// declaration.
static int bind_samsung_jpeg_gainmap(
    lpb_context* context,
    const std::vector<uint8_t>& data,
    uint64_t jpeg_end,
    uint64_t video_offset,
    uint64_t video_length,
    lpb_source_media_facts* facts) noexcept {
    if (!context || !facts || jpeg_end > data.size() ||
        video_offset > data.size() || video_length > data.size() - video_offset) {
        return -1;
    }

    const std::string xmp = extract_xmp_string(context, data, LPB_IMAGE_CONTAINER_JPEG);
    if (xmp.empty()) return 0;

    std::vector<xmp_node> nodes;
    if (!scan_xmp_tree(xmp, nodes)) {
        set_error(context, "Samsung JPEG XMP directory is malformed.");
        return -1;
    }

    container_directory_info directory;
    if (!find_container_directory(nodes, directory)) {
        if (has_attribute_name_in_nodes(nodes, google_camera_namespace, "MotionPhoto")) {
            set_error(context, "Samsung JPEG MotionPhoto XMP directory is malformed.");
            return -1;
        }
        return 0;
    }

    const container_item_info* primary = nullptr;
    const container_item_info* gainmap = nullptr;
    const container_item_info* motion = nullptr;
    size_t primary_count = 0;
    size_t gainmap_count = 0;
    size_t motion_count = 0;
    for (const auto& item : directory.items) {
        if (item.semantic == "Primary") {
            ++primary_count;
            primary = &item;
        } else if (item.semantic == "GainMap") {
            ++gainmap_count;
            gainmap = &item;
        } else if (item.semantic == "MotionPhoto") {
            ++motion_count;
            motion = &item;
        } else {
            set_error(context, "Samsung JPEG XMP directory contains an unsupported item.");
            return -1;
        }
    }

    if (primary_count != 1 || motion_count != 1 || directory.items.front().semantic != "Primary" ||
        directory.items.back().semantic != "MotionPhoto" ||
        (gainmap_count > 1) || (gainmap_count != 0 && directory.items.size() != 3) ||
        (gainmap_count == 0 && directory.items.size() != 2) ||
        primary == nullptr || motion == nullptr ||
        primary->mime != "image/jpeg" || motion->mime != "video/mp4") {
        set_error(context, "Samsung JPEG XMP directory has duplicate, shadowed, or malformed ownership.");
        return -1;
    }

    if (gainmap == nullptr) return 0;
    if (directory.items[1].semantic != "GainMap" || gainmap->mime != "image/jpeg" ||
        !gainmap->has_length || gainmap->malformed_length || gainmap->length == 0 ||
        !primary->has_padding || primary->malformed_padding ||
        primary->malformed_length ||
        !motion->has_length || motion->malformed_length || motion->length == 0 ||
        (primary->has_length && primary->length != 0 && primary->length != jpeg_end) ||
        (motion->has_padding && (motion->malformed_padding || motion->padding != 0)) ||
        gainmap->length > std::numeric_limits<uint64_t>::max() - jpeg_end) {
        set_error(context, "Samsung JPEG GainMap XMP declaration is malformed.");
        return -1;
    }

    const uint64_t gainmap_offset = jpeg_end;
    const uint64_t gainmap_end = gainmap_offset + gainmap->length;
    if (gainmap_end > data.size() || video_offset < gainmap_end ||
        video_offset - gainmap_end != primary->padding ||
        !is_valid_jpeg_media_range(data.data(), data.size(), gainmap_offset, gainmap->length) ||
        video_length == 0 ||
        video_offset + video_length > data.size()) {
        set_error(context, "Samsung JPEG GainMap range does not match the SEF video ownership.");
        return -1;
    }

    // The ordered XMP directory is the stable identity for JPEG auxiliary
    // media (there is no HEIF numeric item id).  Use its one-based directory
    // position rather than the old zero identity so the GainMap cannot be
    // rebound by a caller merely matching a byte range.
    const uint32_t stable_item_id = 2;
    if (!publish_gainmap_auxiliary(facts, data, LPB_IMAGE_CONTAINER_JPEG,
        { gainmap_offset, gainmap->length }, LPB_AUX_REPRESENTATION_EMBEDDED,
        LPB_AUX_OWNER_PRIMARY, stable_item_id, "gain-map", "samsung-jpeg:container:item:2")) {
        set_error(context, "Samsung JPEG GainMap could not be bound to an auxiliary identity.");
        return -1;
    }
    return 1;
}

static bool bind_gainmap_from_heif_auxiliary(lpb_context* context,
    lpb_source_media_facts* facts) noexcept {
    if (!context || !facts) return false;
    constexpr std::string_view apple_gainmap_relationship =
        "urn:com:apple:photo:2020:aux:hdrgainmap";
    constexpr std::string_view samsung_gainmap_relationship =
        "urn:com:samsung:photo:2024:aux:hdrgainmap";
    size_t gainmap_index = std::numeric_limits<size_t>::max();
    for (size_t i = 0; i < facts->auxiliary_count; ++i) {
        const std::string_view relationship(facts->auxiliary_items[i].relationship);
        if (relationship != apple_gainmap_relationship && relationship != samsung_gainmap_relationship) continue;
        if (gainmap_index != std::numeric_limits<size_t>::max()) {
            set_error(context, "HEIF contains duplicate GainMap auxiliary relationships.");
            return false;
        }
        gainmap_index = i;
    }
    if (gainmap_index == std::numeric_limits<size_t>::max()) return true;

    const auto& auxiliary = facts->auxiliary_items[gainmap_index];
    if (!auxiliary.is_present || auxiliary.struct_size < sizeof(lpb_auxiliary_item_facts) ||
        auxiliary.file_range.length == 0 || auxiliary.item_id == 0) {
        set_error(context, "HEIF GainMap auxiliary relationship is not bound to a valid item.");
        return false;
    }
    auto& gain_map = facts->gain_map;
    gain_map.struct_size = sizeof(lpb_gainmap_item_facts);
    gain_map.is_present = 1;
    gain_map.container = auxiliary.container;
    gain_map.representation = static_cast<int32_t>(auxiliary.representation);
    gain_map.ownership = static_cast<int32_t>(auxiliary.ownership);
    gain_map.owner_artifact_role = auxiliary.ownership == LPB_AUX_OWNER_PRIMARY
        ? LPB_ARTIFACT_PRIMARY_IMAGE
        : LPB_ARTIFACT_AUXILIARY_ITEM;
    gain_map.auxiliary_index = static_cast<uint32_t>(gainmap_index);
    gain_map.item_id = auxiliary.item_id;
    gain_map.file_range = auxiliary.file_range;
    strncpy_s(gain_map.relationship, auxiliary.relationship, _TRUNCATE);
    return true;
}

static bool populate_heif_auxiliary(lpb_context* context, const std::vector<uint8_t>& data,
    lpb_source_media_facts* facts) noexcept {
    if (!context || !facts) return false;
    size_t count = 0;
    const lpb_result probe = lpb_heif_enumerate_auxiliary_items(context, data.data(), data.size(), nullptr, 0, &count);
    if (probe != LPB_RESULT_OK && probe != LPB_RESULT_BUFFER_TOO_SMALL) return false;
    if (count > 8) {
        set_error(context, "HEIF auxiliary item count exceeds the source-facts ABI capacity.");
        return false;
    }
    if (count == 0) { facts->auxiliary_count = 0; return true; }
    size_t written = 0;
    if (lpb_heif_enumerate_auxiliary_items(context, data.data(), data.size(), facts->auxiliary_items, 8, &written) != LPB_RESULT_OK || written != count) {
        set_error(context, "HEIF auxiliary item enumeration did not return the declared item count.");
        return false;
    }
    facts->auxiliary_count = static_cast<uint32_t>(written);
    return true;
}

static void set_heif_graph_failure_status(lpb_context* context, uint64_t capability) noexcept
{
    if (!context) return;
    std::string diagnostic;
    {
        std::scoped_lock lock(context->error_mutex);
        diagnostic = context->last_error;
    }
    const bool unsupported = diagnostic.find("Unsupported") != std::string::npos ||
        diagnostic.find("unsupported") != std::string::npos;
    const bool ambiguous = diagnostic.find("duplicate") != std::string::npos ||
        diagnostic.find("Duplicate") != std::string::npos ||
        diagnostic.find("cycle") != std::string::npos ||
        diagnostic.find("Shadow") != std::string::npos ||
        diagnostic.find("shadow") != std::string::npos ||
        diagnostic.find("ambiguous") != std::string::npos ||
        diagnostic.find("Ambiguous") != std::string::npos;
    set_inspection_status(
        context,
        unsupported ? LPB_INSPECTION_FAILURE_UNSUPPORTED :
            ambiguous ? LPB_INSPECTION_FAILURE_AMBIGUOUS : LPB_INSPECTION_FAILURE_MALFORMED,
        LPB_INSPECTION_STAGE_CONTAINER,
        capability);
}

lpb_result inspect_source(
    lpb_context* context,
    const char* primary_path,
    const char* secondary_path,
    lpb_source_media_facts* out_facts,
    std::vector<lpb_confirmed_residue>* out_residues) noexcept
{
    if (!context || !primary_path || !out_facts) {
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    set_inspection_status(context, LPB_INSPECTION_FAILURE_NONE, LPB_INSPECTION_STAGE_NONE);
    // Every non-success path below must remain typed even when an older branch
    // has not supplied a more specific category yet.  Specific ambiguity,
    // unsupported, and I/O decisions override this conservative default.
    set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED, LPB_INSPECTION_STAGE_READ);

    if (out_residues) {
        out_residues->clear();
    }

    if (out_facts->struct_size < sizeof(lpb_source_media_facts)) {
        set_error(context, "out_facts struct_size is smaller than expected lpb_source_media_facts size.");
        set_inspection_status(context, LPB_INSPECTION_FAILURE_INVALID_ARGUMENT, LPB_INSPECTION_STAGE_READ);
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
        set_inspection_status(context, LPB_INSPECTION_FAILURE_IO, LPB_INSPECTION_STAGE_READ);
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    auto primary_data = read_file_bytes(primary_path);
    if (primary_data.empty()) {
        set_error(context, "Failed to read primary file.");
        set_inspection_status(context, LPB_INSPECTION_FAILURE_IO, LPB_INSPECTION_STAGE_READ);
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    primary_size = primary_data.size();
    lpb::crypto::sha256_buffer(primary_data.data(), primary_data.size(), out_facts->primary_sha256);

    lpb_image_container img_cont = detect_image_container(primary_data);
    lpb_video_container vid_cont = detect_video_container(primary_data);
    uint64_t jpeg_end = 0;
    const bool has_jpeg_end = find_jpeg_end(primary_data, jpeg_end);

    if (img_cont == LPB_IMAGE_CONTAINER_JPEG && !has_jpeg_end) {
        set_error(context, "Primary JPEG image is structurally malformed or truncated.");
        set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED, LPB_INSPECTION_STAGE_CONTAINER);
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    std::vector<std::string> xmp_packets;
    if (img_cont == LPB_IMAGE_CONTAINER_JPEG && jpeg_xmp_structure_invalid(primary_data, &xmp_packets)) {
        set_error(context, "JPEG contains duplicate or malformed XMP packet(s).");
        set_inspection_status(context, LPB_INSPECTION_FAILURE_AMBIGUOUS, LPB_INSPECTION_STAGE_METADATA);
        return LPB_RESULT_INVALID_ARGUMENT;
    }

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
            set_inspection_status(context, LPB_INSPECTION_FAILURE_IO, LPB_INSPECTION_STAGE_READ);
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        lpb::crypto::sha256_buffer(sec_data.data(), sec_data.size(), out_facts->secondary_sha256);
        out_facts->has_secondary_source = 1;

        lpb_video_container sec_vid_cont = detect_video_container(sec_data);
        if (sec_vid_cont == LPB_VIDEO_CONTAINER_UNKNOWN) {
            set_error(context, "Secondary file is not a supported video container.");
            set_inspection_status(context, LPB_INSPECTION_FAILURE_UNSUPPORTED, LPB_INSPECTION_STAGE_CONTAINER);
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        // Validate the complete MOV media range before metadata pairing.  A
        // malformed/trailing-byte MOV must remain a container failure; if CID
        // extraction ran first, the generic "missing ContentIdentifier"
        // branch would hide that stronger diagnostic.
        if (sec_vid_cont == LPB_VIDEO_CONTAINER_MOV &&
            !is_valid_mov_container(sec_data.data(), sec_data.size())) {
            set_error(context, "Apple Live Photo secondary MOV video is structurally malformed.");
            set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED,
                LPB_INSPECTION_STAGE_CONTAINER, LPB_CAPABILITY_APPLE);
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        // 1. Check Apple Live Photo Dual-File
        std::string image_content_id;
        std::string video_content_id;
        bool image_has_conflict = false;
        const bool image_id_ok = extract_apple_cid_from_image(context, primary_data, img_cont, image_content_id, image_has_conflict);
        if (image_has_conflict) {
            set_error(context, "Apple image contains conflicting ContentIdentifiers in MakerNote.");
            set_inspection_status(context, LPB_INSPECTION_FAILURE_AMBIGUOUS, LPB_INSPECTION_STAGE_PAIRING, LPB_CAPABILITY_APPLE);
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        const bool video_id_ok = extract_apple_cid_from_video(context, sec_data, video_content_id);

        if (image_id_ok && video_id_ok) {
            if (image_content_id == video_content_id) {
                if (img_cont == LPB_IMAGE_CONTAINER_JPEG) {
                    if (!is_valid_jpeg_media_range(primary_data.data(), primary_data.size(), 0, primary_size)) {
                        set_error(context, "Apple Live Photo primary JPEG image is structurally malformed or truncated.");
                        set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED, LPB_INSPECTION_STAGE_CONTAINER, LPB_CAPABILITY_APPLE);
                        return LPB_RESULT_INVALID_ARGUMENT;
                    }
                } else if (img_cont == LPB_IMAGE_CONTAINER_HEIC) {
                    if (!is_valid_heic_container(context, primary_data.data(), primary_data.size())) {
                        set_error(context, "Apple Live Photo primary HEIC image is structurally malformed.");
                        set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED, LPB_INSPECTION_STAGE_CONTAINER, LPB_CAPABILITY_APPLE);
                        return LPB_RESULT_INVALID_ARGUMENT;
                    }
                    if (!populate_heif_auxiliary(context, primary_data, out_facts)) {
                        set_heif_graph_failure_status(context, LPB_CAPABILITY_APPLE);
                        return LPB_RESULT_INVALID_ARGUMENT;
                    }
                    if (!bind_gainmap_from_heif_auxiliary(context, out_facts)) {
                        set_inspection_status(context, LPB_INSPECTION_FAILURE_AMBIGUOUS,
                            LPB_INSPECTION_STAGE_CONTAINER, LPB_CAPABILITY_APPLE);
                        return LPB_RESULT_INVALID_ARGUMENT;
                    }
                } else {
                    set_error(context, "Apple Live Photo primary image container is unsupported.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }

                if (sec_vid_cont == LPB_VIDEO_CONTAINER_MOV) {
                    if (!is_valid_mov_container(sec_data.data(), sec_data.size())) {
                        set_error(context, "Apple Live Photo secondary MOV video is structurally malformed.");
                        set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED, LPB_INSPECTION_STAGE_CONTAINER, LPB_CAPABILITY_APPLE);
                        return LPB_RESULT_INVALID_ARGUMENT;
                    }
                } else if (sec_vid_cont == LPB_VIDEO_CONTAINER_MP4) {
                    if (!is_valid_isobmff_media_range(sec_data.data(), sec_data.size(), 0, sec_data.size())) {
                        set_error(context, "Apple Live Photo secondary MP4 video is structurally malformed.");
                        set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED, LPB_INSPECTION_STAGE_CONTAINER, LPB_CAPABILITY_APPLE);
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

                if (out_residues) {
                    std::string fp_0011, fp_0017, fp_0025, fp_002b;
                    lpb::protocols::apple::apple_image_get_tag_fingerprint(context, primary_data, img_cont, 0x0011, fp_0011);
                    add_residue(out_residues, "apple-img-makernote-0011", LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO,
                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_EXIF_MAKERNOTE_TAG, "0x0011", "ContentIdentifier", fp_0011.c_str(),
                        LPB_COORD_STRUCTURED_SELECTOR, LPB_REMOVAL_REBUILD_CONTAINER, 1);
                    if (apple_image_has_tag(context, primary_data, img_cont, 0x0017)) {
                        lpb::protocols::apple::apple_image_get_tag_fingerprint(context, primary_data, img_cont, 0x0017, fp_0017);
                        add_residue(out_residues, "apple-img-makernote-0017", LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO,
                            LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_EXIF_MAKERNOTE_TAG, "0x0017", "LivePhotoEntry17", fp_0017.c_str(),
                            LPB_COORD_STRUCTURED_SELECTOR, LPB_REMOVAL_REBUILD_CONTAINER, 1);
                    }
                    if (apple_image_has_tag(context, primary_data, img_cont, 0x0025)) {
                        lpb::protocols::apple::apple_image_get_tag_fingerprint(context, primary_data, img_cont, 0x0025, fp_0025);
                        add_residue(out_residues, "apple-img-makernote-0025", LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO,
                            LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_EXIF_MAKERNOTE_TAG, "0x0025", "LivePhotoEntry25", fp_0025.c_str(),
                            LPB_COORD_STRUCTURED_SELECTOR, LPB_REMOVAL_REBUILD_CONTAINER, 1);
                    }
                    if (apple_image_has_tag(context, primary_data, img_cont, 0x002b)) {
                        lpb::protocols::apple::apple_image_get_tag_fingerprint(context, primary_data, img_cont, 0x002b, fp_002b);
                        add_residue(out_residues, "apple-img-makernote-002b", LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO,
                            LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_EXIF_MAKERNOTE_TAG, "0x002b", "LivePhotoEntry2B", fp_002b.c_str(),
                            LPB_COORD_STRUCTURED_SELECTOR, LPB_REMOVAL_REBUILD_CONTAINER, 1);
                    }
                    std::string fp_cid, fp_lp;
                    if (lpb::containers::mp4_get_mdta_key_fingerprint(sec_data, "com.apple.quicktime.content.identifier", fp_cid)) {
                        add_residue(out_residues, "apple-vid-mdta-cid", LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO,
                            LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.apple.quicktime.content.identifier", "ContentIdentifier", fp_cid.c_str());
                    }
                    if (lpb::containers::mp4_get_mdta_key_fingerprint(sec_data, "com.apple.quicktime.live-photo", fp_lp)) {
                        add_residue(out_residues, "apple-vid-mdta-livephoto", LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO,
                            LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.apple.quicktime.live-photo", "LivePhotoKey", fp_lp.c_str());
                    }
                    if (contains_text_in_moov(sec_data, "com.apple.quicktime.live-photo-info")) {
                        std::string fp = lpb::crypto::compute_metadata_track_fingerprint("meta", "com.apple.quicktime.live-photo-info");
                        add_residue(out_residues, "apple-vid-track-livephoto-info", LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO,
                            LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_METADATA_TRACK, "com.apple.quicktime.live-photo-info", "LivePhotoInfoTrack", fp.c_str());
                    }
                    if (contains_text_in_moov(sec_data, "com.apple.quicktime.still-image-time")) {
                        std::string fp = lpb::crypto::compute_metadata_track_fingerprint("meta", "com.apple.quicktime.still-image-time");
                        add_residue(out_residues, "apple-vid-track-still-image-time", LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO,
                            LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_METADATA_TRACK, "com.apple.quicktime.still-image-time", "StillImageTimeTrack", fp.c_str());
                    }
                    if (contains_text_in_moov(sec_data, "com.apple.quicktime.live-photo-still-image-transform")) {
                        std::string fp = lpb::crypto::compute_metadata_track_fingerprint("meta", "com.apple.quicktime.live-photo-still-image-transform");
                        add_residue(out_residues, "apple-vid-track-transform", LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO,
                            LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_METADATA_TRACK, "com.apple.quicktime.live-photo-still-image-transform", "StillImageTransformTrack", fp.c_str());
                    }
                    if (contains_text_in_moov(sec_data, "com.apple.quicktime.live-photo-still-image-transform-reference-dimensions")) {
                        std::string fp = lpb::crypto::compute_metadata_track_fingerprint("meta", "com.apple.quicktime.live-photo-still-image-transform-reference-dimensions");
                        add_residue(out_residues, "apple-vid-track-reference-dimensions", LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO,
                            LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_METADATA_TRACK, "com.apple.quicktime.live-photo-still-image-transform-reference-dimensions", "TransformReferenceDimensionsTrack", fp.c_str());
                    }
                }
                return LPB_RESULT_OK;
            } else {
                set_error(context, "Apple Live Photo dual-file pairing identifier mismatch.");
                set_inspection_status(context, LPB_INSPECTION_FAILURE_AMBIGUOUS, LPB_INSPECTION_STAGE_PAIRING, LPB_CAPABILITY_APPLE);
                return LPB_RESULT_INVALID_ARGUMENT;
            }
        }
        if (image_id_ok && !video_id_ok) {
            set_error(context, "Apple Live Photo dual-file pairing mismatch: missing ContentIdentifier on secondary video.");
            set_inspection_status(context, LPB_INSPECTION_FAILURE_AMBIGUOUS, LPB_INSPECTION_STAGE_PAIRING, LPB_CAPABILITY_APPLE);
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (!image_id_ok && video_id_ok) {
            set_error(context, "Apple Live Photo dual-file pairing mismatch: missing ContentIdentifier on primary image.");
            set_inspection_status(context, LPB_INSPECTION_FAILURE_AMBIGUOUS, LPB_INSPECTION_STAGE_PAIRING, LPB_CAPABILITY_APPLE);
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

                if (out_residues) {
                    if (contains_text(sec_data, "vivoMediaExtInfo")) {
                        std::string fp_uuid;
                        size_t p_box = 0;
                        while (p_box < sec_data.size()) {
                            isobmff_box_header box{};
                            if (!try_read_box_header(sec_data.data(), p_box, sec_data.size(), box)) break;
                            if (box.size >= 24 && std::memcmp(sec_data.data() + p_box + 4, "uuid", 4) == 0 &&
                                std::memcmp(sec_data.data() + p_box + box.header_size, "vivoMediaExtInfo", 16) == 0) {
                                fp_uuid = lpb::crypto::compute_isobmff_box_fingerprint("uuid", box.size, sec_data.data() + p_box + box.header_size, box.size - box.header_size);
                                break;
                            }
                            p_box += box.size;
                        }
                        add_residue(out_residues, "vivo-legacy-vid-uuid", LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL,
                            LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_UUID_BOX, "vivoMediaExtInfo", "vivoMediaExtInfo", fp_uuid.c_str());
                    }
                    std::string fp_lp, fp_it, fp_gallery;
                    if (lpb::containers::mp4_get_mdta_key_fingerprint(sec_data, "com.android.camera.livephoto", fp_lp)) {
                        add_residue(out_residues, "vivo-legacy-vid-mdta-livephoto", LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL,
                            LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.android.camera.livephoto", "com.android.camera.livephoto", fp_lp.c_str());
                    }
                    if (lpb::containers::mp4_get_mdta_key_fingerprint(sec_data, "com.android.camera.imageTime", fp_it)) {
                        add_residue(out_residues, "vivo-legacy-vid-mdta-imagetime", LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL,
                            LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.android.camera.imageTime", "com.android.camera.imageTime", fp_it.c_str());
                    }
                    if (lpb::containers::mp4_get_mdta_key_fingerprint(sec_data, "com.vivo.gallery.livePhoto", fp_gallery)) {
                        add_residue(out_residues, "vivo-legacy-vid-mdta-gallery", LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL,
                            LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.vivo.gallery.livePhoto", "com.vivo.gallery.livePhoto", fp_gallery.c_str());
                    }
                }
                return LPB_RESULT_OK;
            } else {
                set_error(context, "Vivo legacy dual-file pairing identifier mismatch.");
                set_inspection_status(context, LPB_INSPECTION_FAILURE_AMBIGUOUS,
                    LPB_INSPECTION_STAGE_PAIRING, LPB_CAPABILITY_VIVO_LEGACY);
                return LPB_RESULT_INVALID_ARGUMENT;
            }
        }
        if (vivo_img_ok && !vivo_vid_ok) {
            set_error(context, "Vivo legacy dual-file pairing mismatch: missing pairing identifier on secondary video.");
            set_inspection_status(context, LPB_INSPECTION_FAILURE_AMBIGUOUS,
                LPB_INSPECTION_STAGE_PAIRING, LPB_CAPABILITY_VIVO_LEGACY);
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (!vivo_img_ok && vivo_vid_ok) {
            set_error(context, "Vivo legacy dual-file pairing mismatch: missing pairing identifier on primary image.");
            set_inspection_status(context, LPB_INSPECTION_FAILURE_AMBIGUOUS,
                LPB_INSPECTION_STAGE_PAIRING, LPB_CAPABILITY_VIVO_LEGACY);
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        // If neither file contains Apple or Vivo pairing identity,
        // and both containers are structurally valid, this is a non-live pair.
        if (!image_id_ok && !video_id_ok && !vivo_img_ok && !vivo_vid_ok) {
            if (img_cont == LPB_IMAGE_CONTAINER_JPEG && !has_jpeg_end) {
                set_error(context, "Primary image is a malformed or truncated JPEG.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (img_cont == LPB_IMAGE_CONTAINER_HEIC && !is_valid_heic_container(context, primary_data.data(), primary_data.size())) {
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
        set_inspection_status(context, LPB_INSPECTION_FAILURE_UNSUPPORTED, LPB_INSPECTION_STAGE_PROTOCOL);
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    // Single file checks
    // 1. Huawei / Honor Moving Photo (ends with LIVE_)
    int64_t huawei_cover_time_us = 0;
    bool is_honor = false;
    uint64_t huawei_vid_off = 0, huawei_vid_len = 0;
    if (check_huawei_moving_photo(primary_data, primary_size, huawei_vid_off, huawei_vid_len, huawei_cover_time_us, is_honor)) {
        const bool primary_valid = img_cont == LPB_IMAGE_CONTAINER_JPEG
            // The protocol report defines the embedded MP4 as an ftyp box
            // located after JPEG EOI; real Huawei/Honor files may keep
            // vendor-owned bytes between those two boundaries.  LIVE_ plus
            // the exact MP4 range claims that composite prefix, so adjacency
            // is not a protocol requirement.
            ? (has_jpeg_end && jpeg_end <= huawei_vid_off &&
               is_valid_jpeg_media_range(primary_data.data(), primary_data.size(), 0, jpeg_end))
            : img_cont == LPB_IMAGE_CONTAINER_HEIC
                ? is_valid_heic_container(context, primary_data.data(), static_cast<size_t>(huawei_vid_off))
                : false;
        if (!primary_valid) {
            set_error(context, "Huawei/Honor primary image range is malformed.");
            set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED,
                LPB_INSPECTION_STAGE_CONTAINER, LPB_CAPABILITY_HUAWEI_HONOR);
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        if (img_cont == LPB_IMAGE_CONTAINER_JPEG && !xmp_packets.empty()) {
            std::vector<xmp_node> huawei_xmp_nodes;
            bool huawei_xmp_valid = true;
            for (const auto& packet : xmp_packets) {
                if (!scan_xmp_tree(packet, huawei_xmp_nodes)) {
                    huawei_xmp_valid = false;
                    break;
                }
            }
            if (!huawei_xmp_valid) {
                set_error(context, "Huawei/Honor JPEG contains malformed XMP metadata.");
                set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED,
                    LPB_INSPECTION_STAGE_METADATA, LPB_CAPABILITY_HUAWEI_HONOR);
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            container_directory_info directory;
            if (find_container_directory(huawei_xmp_nodes, directory)) {
                const container_item_info* gainmap_item = nullptr;
                for (const auto& item : directory.items) {
                    if (item.semantic != "GainMap") continue;
                    if (gainmap_item != nullptr) {
                        set_error(context, "Huawei/Honor Container:Directory contains duplicate GainMap items.");
                        set_inspection_status(context, LPB_INSPECTION_FAILURE_AMBIGUOUS,
                            LPB_INSPECTION_STAGE_METADATA, LPB_CAPABILITY_HUAWEI_HONOR);
                        return LPB_RESULT_INVALID_ARGUMENT;
                    }
                    gainmap_item = &item;
                }
                if (gainmap_item != nullptr) {
                    if (gainmap_item->mime != "image/jpeg" || !gainmap_item->has_length ||
                        gainmap_item->malformed_length || gainmap_item->length == 0 ||
                        gainmap_item->length > huawei_vid_off - jpeg_end ||
                        !is_valid_jpeg_media_range(primary_data.data(), primary_data.size(),
                            jpeg_end, gainmap_item->length)) {
                        set_error(context, "Huawei/Honor GainMap directory item does not own a valid JPEG range after Primary EOI.");
                        set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED,
                            LPB_INSPECTION_STAGE_CONTAINER, LPB_CAPABILITY_HUAWEI_HONOR);
                        return LPB_RESULT_INVALID_ARGUMENT;
                    }
                    out_facts->gain_map.is_present = 1;
                    out_facts->gain_map.container = LPB_IMAGE_CONTAINER_JPEG;
                    out_facts->gain_map.file_range.offset = jpeg_end;
                    out_facts->gain_map.file_range.length = gainmap_item->length;
                    if (!publish_gainmap_auxiliary(out_facts, primary_data, LPB_IMAGE_CONTAINER_JPEG,
                            out_facts->gain_map.file_range)) {
                        set_error(context, "Huawei/Honor GainMap could not be bound to an auxiliary identity.");
                        return LPB_RESULT_INVALID_ARGUMENT;
                    }
                }
            }
        }
        out_facts->protocol = is_honor ? LPB_SOURCE_PROTOCOL_HONOR_MOVING_PHOTO : LPB_SOURCE_PROTOCOL_HUAWEI_MOVING_PHOTO;
        out_facts->primary_image.file_range.offset = 0;
        // The protocol report defines JPEG extraction at the first EOI.  Any
        // vendor-owned bytes before the embedded ftyp are part of the live
        // wrapper, not part of the neutral primary JPEG artifact.
        out_facts->primary_image.file_range.length = img_cont == LPB_IMAGE_CONTAINER_JPEG
            ? jpeg_end
            : huawei_vid_off;
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

        if (out_residues) {
            const auto p = out_facts->protocol;
            if (out_facts->protocol_tail_range.length > 0) {
                const uint8_t* tail_bytes = primary_data.data() + out_facts->protocol_tail_range.offset;
                size_t tail_len = static_cast<size_t>(out_facts->protocol_tail_range.length);
                std::string fp_tail = lpb::crypto::compute_tail_range_fingerprint("LIVE_", tail_bytes, tail_len);
                add_residue(out_residues, "huawei-img-tail-live", p,
                    LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_PROTOCOL_TAIL_RANGE, "tail:LIVE_", "LiveTail", fp_tail.c_str(),
                    LPB_COORD_ORIGINAL_SOURCE_RANGE, LPB_REMOVAL_DELETE, 0);
            }
            std::span<const uint8_t> vid_span(primary_data.data() + huawei_vid_off, static_cast<size_t>(huawei_vid_len));
            std::string fp_oh, fp_hw, fp_ct;
            if (lpb::containers::mp4_get_mdta_key_fingerprint(vid_span, "com.openharmony.movingphoto", fp_oh)) {
                add_residue(out_residues, "huawei-vid-mdta-openharmony", p,
                    LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.openharmony.movingphoto", "com.openharmony.movingphoto", fp_oh.c_str(),
                    LPB_COORD_STRUCTURED_SELECTOR, LPB_REMOVAL_DELETE, 1);
            }
            if (lpb::containers::mp4_get_mdta_key_fingerprint(vid_span, "com.huawei.movingphoto", fp_hw)) {
                add_residue(out_residues, "huawei-vid-mdta-huawei", p,
                    LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.huawei.movingphoto", "com.huawei.movingphoto", fp_hw.c_str(),
                    LPB_COORD_STRUCTURED_SELECTOR, LPB_REMOVAL_DELETE, 1);
            }
            if (lpb::containers::mp4_get_mdta_key_fingerprint(vid_span, "com.openharmony.covertime", fp_ct)) {
                add_residue(out_residues, "huawei-vid-mdta-covertime", p,
                    LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.openharmony.covertime", "com.openharmony.covertime", fp_ct.c_str(),
                    LPB_COORD_STRUCTURED_SELECTOR, LPB_REMOVAL_DELETE, 1);
            }
            if (contains_text_in_moov(vid_span, "com.openharmony.timed_metadata.movingphoto")) {
                std::string fp_tr = lpb::crypto::compute_metadata_track_fingerprint("meta", "com.openharmony.timed_metadata.movingphoto");
                add_residue(out_residues, "huawei-vid-track-movingphoto", p,
                    LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_METADATA_TRACK, "com.openharmony.timed_metadata.movingphoto", "com.openharmony.timed_metadata.movingphoto", fp_tr.c_str(),
                    LPB_COORD_STRUCTURED_SELECTOR, LPB_REMOVAL_DELETE, 1);
            }
        }
        return LPB_RESULT_OK;
    } else if (primary_data.size() >= 20 &&
               std::memcmp(primary_data.data() + primary_data.size() - 20, "LIVE_", 5) == 0) {
        set_error(context, "Huawei/Honor Moving Photo trailer is malformed or video range is corrupt.");
        set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED,
            LPB_INSPECTION_STAGE_PROTOCOL, LPB_CAPABILITY_HUAWEI_HONOR);
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    // 2. Samsung JPEG (SEFT Trailer)
    if (img_cont == LPB_IMAGE_CONTAINER_JPEG && primary_data.size() >= 8 &&
        std::memcmp(primary_data.data() + primary_data.size() - 4, "SEFT", 4) == 0) {
        uint64_t sef_vid_off = 0, sef_vid_len = 0;
        int sef_res = check_samsung_sef_jpeg(context, primary_data, sef_vid_off, sef_vid_len);
        if (sef_res < 0) {
            set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED,
                LPB_INSPECTION_STAGE_PROTOCOL, LPB_CAPABILITY_SAMSUNG_JPEG);
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (sef_res > 0) {
            if (!is_valid_jpeg_media_range(primary_data.data(), primary_data.size(), 0, jpeg_end) ||
                !is_valid_isobmff_media_range(primary_data.data(), primary_data.size(), sef_vid_off, sef_vid_len)) {
                set_error(context, "Samsung JPEG primary or embedded video range is malformed.");
                set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED,
                    LPB_INSPECTION_STAGE_CONTAINER, LPB_CAPABILITY_SAMSUNG_JPEG);
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            out_facts->protocol = LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG;
            out_facts->primary_image.file_range.offset = 0;
            out_facts->primary_image.file_range.length = jpeg_end;
            out_facts->motion_video.is_present = 1;
            out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
            out_facts->motion_video.file_range.offset = sef_vid_off;
            out_facts->motion_video.file_range.length = sef_vid_len;

            const int gainmap_result = bind_samsung_jpeg_gainmap(
                context, primary_data, jpeg_end, sef_vid_off, sef_vid_len, out_facts);
            if (gainmap_result < 0) {
                set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED,
                    LPB_INSPECTION_STAGE_CONTAINER, LPB_CAPABILITY_SAMSUNG_JPEG);
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            if (!publish_samsung_sef_preservation_carriers(
                    out_facts, primary_data, jpeg_end, false)) {
                set_error(context, "Samsung non-motion SEF preservation entries could not be represented.");
                set_inspection_status(context, LPB_INSPECTION_FAILURE_AMBIGUOUS,
                    LPB_INSPECTION_STAGE_CONTAINER, LPB_CAPABILITY_SAMSUNG_JPEG);
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            if (out_residues) {
                std::string fp_0a30, fp_0a31;
                lpb::protocols::samsung_sef_get_entry_fingerprint(primary_data.data(), primary_data.size(), 0x0A30, fp_0a30);
                add_residue(out_residues, "samsung-jpeg-sef-0a30", LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG,
                    LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_SEF_ENTRY, "0x0A30:MotionPhoto_Data", "MotionPhoto_Data", fp_0a30.c_str(),
                    LPB_COORD_STRUCTURED_SELECTOR, LPB_REMOVAL_REBUILD_CONTAINER, 1);
                if (lpb_samsung_sef_has_tag(primary_data.data(), primary_data.size(), 0x0A31)) {
                    lpb::protocols::samsung_sef_get_entry_fingerprint(primary_data.data(), primary_data.size(), 0x0A31, fp_0a31);
                    add_residue(out_residues, "samsung-jpeg-sef-0a31", LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG,
                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_SEF_ENTRY, "0x0A31:MotionPhoto_Version", "MotionPhoto_Version", fp_0a31.c_str(),
                        LPB_COORD_STRUCTURED_SELECTOR, LPB_REMOVAL_REBUILD_CONTAINER, 1);
                }
                std::string xmp_s = extract_xmp_string(context, primary_data, img_cont);
                if (!xmp_s.empty()) {
                    std::vector<xmp_node> xmp_nodes;
                    if (scan_xmp_tree(xmp_s, xmp_nodes)) {
                        if (has_attribute_name_in_nodes(xmp_nodes, google_camera_namespace, "MotionPhoto")) {
                            std::string val = get_attribute_value_in_nodes(xmp_nodes, google_camera_namespace, "MotionPhoto");
                            std::string fp = lpb::crypto::compute_xmp_property_fingerprint(google_camera_namespace, "MotionPhoto", val);
                            add_residue(out_residues, "samsung-jpeg-xmp-motionphoto", LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG,
                                LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "GCamera:MotionPhoto", "MotionPhoto", fp.c_str());
                        }
                        if (has_attribute_name_in_nodes(xmp_nodes, google_camera_namespace, "MotionPhotoVersion")) {
                            std::string val = get_attribute_value_in_nodes(xmp_nodes, google_camera_namespace, "MotionPhotoVersion");
                            std::string fp = lpb::crypto::compute_xmp_property_fingerprint(google_camera_namespace, "MotionPhotoVersion", val);
                            add_residue(out_residues, "samsung-jpeg-xmp-version", LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG,
                                LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "GCamera:MotionPhotoVersion", "MotionPhotoVersion", fp.c_str());
                        }
                        if (has_attribute_name_in_nodes(xmp_nodes, google_camera_namespace, "MotionPhotoPresentationTimestampUs")) {
                            std::string val = get_attribute_value_in_nodes(xmp_nodes, google_camera_namespace, "MotionPhotoPresentationTimestampUs");
                            std::string fp = lpb::crypto::compute_xmp_property_fingerprint(google_camera_namespace, "MotionPhotoPresentationTimestampUs", val);
                            add_residue(out_residues, "samsung-jpeg-xmp-pts", LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG,
                                LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "GCamera:MotionPhotoPresentationTimestampUs", "MotionPhotoPresentationTimestampUs", fp.c_str());
                        }
                        container_directory_info s_dir;
                        if (find_container_directory(xmp_nodes, s_dir)) {
                            for (const auto& item : s_dir.items) {
                                if (item.semantic == "MotionPhoto") {
                                    std::string fp = lpb::crypto::compute_xmp_container_item_fingerprint(
                                        item.semantic, item.mime, item.length, item.padding, item.has_padding);
                                    add_residue(out_residues, "samsung-jpeg-container-item-motionphoto", LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG,
                                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_CONTAINER_ITEM, "Item:Semantic=MotionPhoto", "MotionPhoto", fp.c_str());
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            return LPB_RESULT_OK;
        }

        // lpb_samsung_sef_parse validates the complete SEFH/SEFT directory
        // and every payload before reporting the distinguished "no
        // MotionPhoto_Data" result.  A rebuilt trailer containing only
        // non-live Samsung entries is therefore a valid neutral JPEG
        // container, not an unexplained byte tail.
        out_facts->protocol = LPB_SOURCE_PROTOCOL_NON_LIVE;
        out_facts->primary_image.is_present = 1;
        out_facts->primary_image.file_range.offset = 0;
        out_facts->primary_image.file_range.length = primary_size;
        if (jpeg_end >= primary_size ||
            !is_valid_non_motion_sef_range(primary_data, jpeg_end, primary_size - jpeg_end) ||
            !publish_samsung_sef_preservation_carriers(
                out_facts, primary_data, jpeg_end, true)) {
            set_error(context, "Samsung non-motion SEF trailer could not be represented as a preservation carrier.");
            set_inspection_status(context, LPB_INSPECTION_FAILURE_AMBIGUOUS,
                LPB_INSPECTION_STAGE_CONTAINER, LPB_CAPABILITY_SAMSUNG_JPEG);
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        // The remaining structured SEF entries are preserved non-live
        // auxiliary metadata, not an active live-photo protocol tail.
        out_facts->protocol_tail_range.offset = 0;
        out_facts->protocol_tail_range.length = 0;
        return LPB_RESULT_OK;
    }

    // 3. Samsung HEIC (sefd box)
    if (img_cont == LPB_IMAGE_CONTAINER_HEIC) {
        bool has_sefd_box = false;
        bool has_nested_sefd_box = false;
        bool has_mpvd_box = false;
        isobmff_box_header sefd_box_hdr{};
        isobmff_box_header mpvd_box_hdr{};
        size_t bpos = 0;
        while (bpos + 8 <= primary_data.size()) {
            isobmff_box_header bh{};
            if (!try_read_box_header(primary_data.data(), bpos, primary_data.size(), bh)) break;
            if (std::memcmp(primary_data.data() + bpos + 4, "sefd", 4) == 0) {
                has_sefd_box = true;
                sefd_box_hdr = bh;
            } else if (std::memcmp(primary_data.data() + bpos + 4, "mpvd", 4) == 0) {
                has_mpvd_box = true;
                mpvd_box_hdr = bh;
                size_t nested_pos = bh.start + bh.header_size;
                const size_t nested_end = bh.start + bh.size;
                while (nested_pos < nested_end) {
                    isobmff_box_header nested{};
                    if (!try_read_box_header(primary_data.data(), nested_pos, nested_end, nested)) break;
                    if (std::memcmp(primary_data.data() + nested_pos + 4, "sefd", 4) == 0) {
                        has_nested_sefd_box = true;
                        break;
                    }
                    nested_pos += nested.size;
                }
            }
            bpos += bh.size;
        }
        if (has_sefd_box || has_nested_sefd_box) {
            uint64_t heic_vid_off = 0, heic_vid_len = 0;
            if (!check_samsung_sef_heic(primary_data, heic_vid_off, heic_vid_len, sefd_box_hdr)) {
                set_error(context, "Samsung HEIC sefd box or SEF directory is malformed.");
                set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED,
                    LPB_INSPECTION_STAGE_PROTOCOL, LPB_CAPABILITY_SAMSUNG_HEIC);
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (!populate_heif_auxiliary(context, primary_data, out_facts)) {
                set_heif_graph_failure_status(context, LPB_CAPABILITY_SAMSUNG_HEIC);
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (!bind_gainmap_from_heif_auxiliary(context, out_facts)) {
                set_inspection_status(context, LPB_INSPECTION_FAILURE_AMBIGUOUS,
                    LPB_INSPECTION_STAGE_CONTAINER, LPB_CAPABILITY_SAMSUNG_HEIC);
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            out_facts->protocol = LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC;
            out_facts->primary_image.file_range.offset = 0;
            out_facts->primary_image.file_range.length = primary_size;
            out_facts->motion_video.is_present = 1;
            out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
            out_facts->motion_video.file_range.offset = heic_vid_off;
            out_facts->motion_video.file_range.length = heic_vid_len;

            if (out_residues) {
                if (has_mpvd_box) {
                    std::string fp_mpvd = lpb::crypto::compute_isobmff_box_fingerprint(
                        "mpvd", mpvd_box_hdr.size, primary_data.data() + mpvd_box_hdr.start, mpvd_box_hdr.size);
                    add_residue(out_residues, "samsung-heic-box-mpvd", LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC,
                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_ISOBMFF_BOX, "mpvd", "mpvd", fp_mpvd.c_str());
                }
                if (sefd_box_hdr.size != 0) {
                    std::string fp_sefd = lpb::crypto::compute_isobmff_box_fingerprint(
                        "sefd", sefd_box_hdr.size, primary_data.data() + sefd_box_hdr.start, sefd_box_hdr.size);
                    add_residue(out_residues, "samsung-heic-box-sefd-motion", LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC,
                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_ISOBMFF_BOX, "sefd", "sefd", fp_sefd.c_str());
                }
                std::string xmp_s = extract_xmp_string(context, primary_data, img_cont);
                if (!xmp_s.empty()) {
                    std::vector<xmp_node> xmp_nodes;
                    if (scan_xmp_tree(xmp_s, xmp_nodes)) {
                        if (has_attribute_name_in_nodes(xmp_nodes, google_camera_namespace, "MotionPhoto")) {
                            std::string val = get_attribute_value_in_nodes(xmp_nodes, google_camera_namespace, "MotionPhoto");
                            std::string fp = lpb::crypto::compute_xmp_property_fingerprint(google_camera_namespace, "MotionPhoto", val);
                            add_residue(out_residues, "samsung-heic-xmp-motionphoto", LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC,
                                LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "GCamera:MotionPhoto", "MotionPhoto", fp.c_str());
                        }
                        if (has_attribute_name_in_nodes(xmp_nodes, google_camera_namespace, "MotionPhotoVersion")) {
                            std::string val = get_attribute_value_in_nodes(xmp_nodes, google_camera_namespace, "MotionPhotoVersion");
                            std::string fp = lpb::crypto::compute_xmp_property_fingerprint(google_camera_namespace, "MotionPhotoVersion", val);
                            add_residue(out_residues, "samsung-heic-xmp-version", LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC,
                                LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "GCamera:MotionPhotoVersion", "MotionPhotoVersion", fp.c_str());
                        }
                        if (has_attribute_name_in_nodes(xmp_nodes, google_camera_namespace, "MotionPhotoPresentationTimestampUs")) {
                            std::string val = get_attribute_value_in_nodes(xmp_nodes, google_camera_namespace, "MotionPhotoPresentationTimestampUs");
                            std::string fp = lpb::crypto::compute_xmp_property_fingerprint(google_camera_namespace, "MotionPhotoPresentationTimestampUs", val);
                            add_residue(out_residues, "samsung-heic-xmp-pts", LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC,
                                LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "GCamera:MotionPhotoPresentationTimestampUs", "MotionPhotoPresentationTimestampUs", fp.c_str());
                        }
                        container_directory_info s_dir;
                        if (find_container_directory(xmp_nodes, s_dir)) {
                            for (const auto& item : s_dir.items) {
                                if (item.semantic == "MotionPhoto") {
                                    std::string fp = lpb::crypto::compute_xmp_container_item_fingerprint(
                                        item.semantic, item.mime, item.length, item.padding, item.has_padding);
                                    add_residue(out_residues, "samsung-heic-container-item-motionphoto", LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC,
                                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_CONTAINER_ITEM, "Item:Semantic=MotionPhoto", "MotionPhoto", fp.c_str());
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            return LPB_RESULT_OK;
        }
    }

    // 4. XMP-based protocols
    if (img_cont == LPB_IMAGE_CONTAINER_HEIC) {
        bool heif_xmp_invalid = false;
        std::string heif_xmp = extract_xmp_string(context, primary_data, img_cont, &heif_xmp_invalid);
        if (heif_xmp_invalid) {
            set_error(context, "HEIF contains malformed, duplicate, shadowed, or conflicting XMP items.");
            set_inspection_status(context, LPB_INSPECTION_FAILURE_AMBIGUOUS,
                LPB_INSPECTION_STAGE_METADATA);
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (!heif_xmp.empty()) xmp_packets.push_back(std::move(heif_xmp));
    }
    std::vector<xmp_node> nodes;
    bool xmp_parsed = false;
    if (!xmp_packets.empty()) {
        xmp_parsed = true;
        for (const auto& packet : xmp_packets) {
            if (!scan_xmp_tree(packet, nodes)) {
                xmp_parsed = false;
                break;
            }
        }
        if (!xmp_parsed) {
            const bool contains_live_property = std::any_of(xmp_packets.begin(), xmp_packets.end(), [](const std::string& packet) {
                return packet.find("MotionPhoto") != std::string::npos ||
                    packet.find("VideoLength") != std::string::npos ||
                    packet.find("MicroVideoOffset") != std::string::npos ||
                    packet.find("VMotionPhotoVersion") != std::string::npos;
            });
            if (contains_live_property) {
                set_error(context, "Source image contains malformed or unparseable Live Photo XMP.");
                set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED,
                    LPB_INSPECTION_STAGE_METADATA);
                return LPB_RESULT_INVALID_ARGUMENT;
            }
        }
    }

    size_t xmp_owner_description = k_no_parent;
    if (xmp_parsed && !nodes.empty() &&
        !validate_xmp_protocol_ownership(nodes, xmp_owner_description)) {
        set_error(context, "Live Photo XMP properties and Container:Directory do not share one rdf:Description owner.");
        set_inspection_status(context, LPB_INSPECTION_FAILURE_AMBIGUOUS,
            LPB_INSPECTION_STAGE_METADATA);
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    if (xmp_parsed && !nodes.empty()) {
        // Vivo X300+
        const bool is_vivo_candidate = has_attribute_name_in_nodes(nodes, vivo_camera_namespace, "VMotionPhotoVersion") ||
            has_attribute_name_in_nodes(nodes, vivo_camera_namespace, "VMotionPhotoFlags");
        if (is_vivo_candidate) {
            set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED,
                LPB_INSPECTION_STAGE_PROTOCOL, LPB_CAPABILITY_VIVO_X300);
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
            if (!check_vivo_x300(nodes, xmp_owner_description, primary_size,
                    pri_len, gm_off, gm_len, vid_off, vid_len)) {
                set_error(context, "Vivo X300+ XMP contains invalid or missing container directory or malformed items.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (gm_len != 0 && !is_valid_jpeg_media_range(primary_data.data(), primary_data.size(), gm_off, gm_len)) {
                set_error(context, "Vivo X300+ GainMap range is not a valid JPEG.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            uint64_t observed_primary_end = 0;
            // The XMP directory's Primary boundary is authoritative only when
            // it is exactly the complete JPEG image.  A valid vendor tail
            // after a truncated/corrupt primary must not be reclassified as a
            // Live Photo merely because the declared GainMap and MP4 ranges
            // happen to fit in the file.
            if (!find_jpeg_end(primary_data, observed_primary_end) ||
                observed_primary_end != pri_len || pri_len != gm_off ||
                observed_primary_end > gm_off ||
                !is_valid_jpeg_media_range(primary_data.data(), primary_data.size(), 0, pri_len)) {
                set_error(context, "Vivo X300+ Primary range is not the complete declared JPEG image.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (!is_valid_isobmff_media_range(primary_data.data(), primary_data.size(), vid_off, vid_len)) {
                set_error(context, "Vivo X300+ video range is not a valid ISO-BMFF MP4.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            out_facts->protocol = LPB_SOURCE_PROTOCOL_VIVO_X300;
            out_facts->primary_image.file_range.offset = 0;
            out_facts->primary_image.file_range.length = pri_len;
            if (gm_len != 0) {
                out_facts->gain_map.is_present = 1;
                out_facts->gain_map.container = LPB_IMAGE_CONTAINER_JPEG;
                out_facts->gain_map.file_range.offset = gm_off;
                out_facts->gain_map.file_range.length = gm_len;
                if (!publish_gainmap_auxiliary(out_facts, primary_data, LPB_IMAGE_CONTAINER_JPEG,
                        out_facts->gain_map.file_range)) {
                    set_error(context, "Vivo X300+ GainMap could not be bound to an auxiliary identity.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
            }
            out_facts->motion_video.is_present = 1;
            out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
            out_facts->motion_video.file_range.offset = vid_off;
            out_facts->motion_video.file_range.length = vid_len;
            int64_t cover_time = 0;
            if (get_global_attribute_i64(nodes, google_camera_namespace, "MotionPhotoPresentationTimestampUs", cover_time) > 0) {
                out_facts->timing.cover_timestamp_us = cover_time;
            }

            if (out_residues) {
                if (has_attribute_name_in_nodes(nodes, google_camera_namespace, "MotionPhoto")) {
                    std::string val = get_attribute_value_in_nodes(nodes, google_camera_namespace, "MotionPhoto");
                    std::string fp = lpb::crypto::compute_xmp_property_fingerprint(google_camera_namespace, "MotionPhoto", val);
                    add_residue(out_residues, "google-v2-xmp-motionphoto", LPB_SOURCE_PROTOCOL_VIVO_X300,
                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "GCamera:MotionPhoto", "MotionPhoto", fp.c_str());
                }
                if (has_attribute_name_in_nodes(nodes, google_camera_namespace, "MotionPhotoVersion")) {
                    std::string val = get_attribute_value_in_nodes(nodes, google_camera_namespace, "MotionPhotoVersion");
                    std::string fp = lpb::crypto::compute_xmp_property_fingerprint(google_camera_namespace, "MotionPhotoVersion", val);
                    add_residue(out_residues, "google-v2-xmp-version", LPB_SOURCE_PROTOCOL_VIVO_X300,
                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "GCamera:MotionPhotoVersion", "MotionPhotoVersion", fp.c_str());
                }
                if (has_attribute_name_in_nodes(nodes, vivo_camera_namespace, "VMotionPhotoVersion")) {
                    std::string val = get_attribute_value_in_nodes(nodes, vivo_camera_namespace, "VMotionPhotoVersion");
                    std::string fp = lpb::crypto::compute_xmp_property_fingerprint(vivo_camera_namespace, "VMotionPhotoVersion", val);
                    add_residue(out_residues, "vivo-xmp-version", LPB_SOURCE_PROTOCOL_VIVO_X300,
                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "VMotionPhotoVersion", "VMotionPhotoVersion", fp.c_str());
                }
                if (has_attribute_name_in_nodes(nodes, vivo_camera_namespace, "VMotionPhotoSource")) {
                    std::string val = get_attribute_value_in_nodes(nodes, vivo_camera_namespace, "VMotionPhotoSource");
                    std::string fp = lpb::crypto::compute_xmp_property_fingerprint(vivo_camera_namespace, "VMotionPhotoSource", val);
                    add_residue(out_residues, "vivo-xmp-source", LPB_SOURCE_PROTOCOL_VIVO_X300,
                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "VMotionPhotoSource", "VMotionPhotoSource", fp.c_str());
                }
                if (has_attribute_name_in_nodes(nodes, vivo_camera_namespace, "VMotionPhotoFlags")) {
                    std::string val = get_attribute_value_in_nodes(nodes, vivo_camera_namespace, "VMotionPhotoFlags");
                    std::string fp = lpb::crypto::compute_xmp_property_fingerprint(vivo_camera_namespace, "VMotionPhotoFlags", val);
                    add_residue(out_residues, "vivo-xmp-flags", LPB_SOURCE_PROTOCOL_VIVO_X300,
                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "VMotionPhotoFlags", "VMotionPhotoFlags", fp.c_str());
                }
                if (has_attribute_name_in_nodes(nodes, vivo_camera_namespace, "VMediaKitVersion")) {
                    std::string val = get_attribute_value_in_nodes(nodes, vivo_camera_namespace, "VMediaKitVersion");
                    std::string fp = lpb::crypto::compute_xmp_property_fingerprint(vivo_camera_namespace, "VMediaKitVersion", val);
                    add_residue(out_residues, "vivo-xmp-mediakit", LPB_SOURCE_PROTOCOL_VIVO_X300,
                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "VMediaKitVersion", "VMediaKitVersion", fp.c_str());
                }
                if (has_attribute_name_in_nodes(nodes, google_camera_namespace, "MotionPhotoPresentationTimestampUs")) {
                    std::string val = get_attribute_value_in_nodes(nodes, google_camera_namespace, "MotionPhotoPresentationTimestampUs");
                    std::string fp = lpb::crypto::compute_xmp_property_fingerprint(google_camera_namespace, "MotionPhotoPresentationTimestampUs", val);
                    add_residue(out_residues, "google-v2-xmp-pts", LPB_SOURCE_PROTOCOL_VIVO_X300,
                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "GCamera:MotionPhotoPresentationTimestampUs", "MotionPhotoPresentationTimestampUs", fp.c_str());
                }
                container_directory_info v_dir;
                if (find_container_directory(nodes, v_dir, xmp_owner_description)) {
                    for (const auto& item : v_dir.items) {
                        if (item.semantic == "MotionPhoto") {
                            std::string fp = lpb::crypto::compute_xmp_container_item_fingerprint(
                                item.semantic, item.mime, item.length, item.padding, item.has_padding);
                            add_residue(out_residues, "google-v2-container-item-motionphoto", LPB_SOURCE_PROTOCOL_VIVO_X300,
                                LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_CONTAINER_ITEM, "Item:Semantic=MotionPhoto", "MotionPhoto", fp.c_str());
                            break;
                        }
                    }
                }
            }
            return LPB_RESULT_OK;
        }

        // OPPO / OnePlus Live Photo
        const bool is_oppo_candidate = has_attribute_name_in_nodes(nodes, oppo_camera_namespace, "VideoLength") ||
            has_attribute_name_in_nodes(nodes, oppo_camera_namespace, "MotionPhotoOwner") ||
            has_attribute_name_in_nodes(nodes, oppo_camera_namespace, "OLivePhotoVersion");
        if (is_oppo_candidate) {
            set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED,
                LPB_INSPECTION_STAGE_PROTOCOL, LPB_CAPABILITY_OPPO);
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
            if (!find_container_directory(nodes, dir, xmp_owner_description) || dir.items.empty()) {
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
                if (!publish_auxiliary_item(
                        out_facts, primary_data, LPB_IMAGE_CONTAINER_JPEG,
                        { orig_off, orig_len }, LPB_AUX_REPRESENTATION_MATERIALIZED,
                        LPB_AUX_OWNER_PRIMARY, 1, "Original", "Original",
                        "oppo:container:item:Original", "primary:0", LPB_AUX_CODEC_JPEG)) {
                    set_error(context, "OPPO Original could not be bound to a unique auxiliary identity.");
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
                if (!publish_gainmap_auxiliary(out_facts, primary_data, LPB_IMAGE_CONTAINER_JPEG,
                        out_facts->gain_map.file_range)) {
                    set_error(context, "OPPO GainMap could not be bound to an auxiliary identity.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
                next_res_offset = gm_off;
            }

            if (!has_jpeg_end || jpeg_end > next_res_offset ||
                !is_valid_jpeg_media_range(primary_data.data(), primary_data.size(), 0, jpeg_end)) {
                set_error(context, "OPPO primary JPEG boundary is malformed or overlaps the next resource.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            // The verified OPPO sample carries a 16-byte zero alignment gap
            // before the first appended resource even though its Primary item
            // omits Padding.  Treat only an all-zero gap as vendor alignment;
            // never absorb it into the neutral JPEG artifact.
            if (jpeg_end < next_res_offset) {
                const auto gap_begin = primary_data.begin() + static_cast<std::ptrdiff_t>(jpeg_end);
                const auto gap_end = primary_data.begin() + static_cast<std::ptrdiff_t>(next_res_offset);
                if (!std::all_of(gap_begin, gap_end, [](uint8_t byte) { return byte == 0; })) {
                    set_error(context, "OPPO contains undeclared non-zero bytes between Primary JPEG and the next resource.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
            }

            out_facts->primary_image.file_range.offset = 0;
            out_facts->primary_image.file_range.length = jpeg_end;

            int64_t cover_time = 0;
            if (get_global_attribute_i64(nodes, oppo_camera_namespace, "MotionPhotoPrimaryPresentationTimestampUs", cover_time) > 0 ||
                get_global_attribute_i64(nodes, google_camera_namespace, "MotionPhotoPresentationTimestampUs", cover_time) > 0) {
                out_facts->timing.cover_timestamp_us = cover_time;
            }

            if (out_residues) {
                if (has_attribute_name_in_nodes(nodes, oppo_camera_namespace, "OLivePhotoVersion")) {
                    std::string val = get_attribute_value_in_nodes(nodes, oppo_camera_namespace, "OLivePhotoVersion");
                    std::string fp = lpb::crypto::compute_xmp_property_fingerprint(oppo_camera_namespace, "OLivePhotoVersion", val);
                    add_residue(out_residues, "oppo-xmp-version", LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO,
                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "OLivePhotoVersion", "OLivePhotoVersion", fp.c_str());
                }
                if (has_attribute_name_in_nodes(nodes, oppo_camera_namespace, "VideoLength")) {
                    std::string val = get_attribute_value_in_nodes(nodes, oppo_camera_namespace, "VideoLength");
                    std::string fp = lpb::crypto::compute_xmp_property_fingerprint(oppo_camera_namespace, "VideoLength", val);
                    add_residue(out_residues, "oppo-xmp-videolength", LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO,
                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "VideoLength", "VideoLength", fp.c_str());
                }
                if (has_attribute_name_in_nodes(nodes, oppo_camera_namespace, "MotionPhotoOwner")) {
                    std::string val = get_attribute_value_in_nodes(nodes, oppo_camera_namespace, "MotionPhotoOwner");
                    std::string fp = lpb::crypto::compute_xmp_property_fingerprint(oppo_camera_namespace, "MotionPhotoOwner", val);
                    add_residue(out_residues, "oppo-xmp-owner", LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO,
                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "MotionPhotoOwner", "MotionPhotoOwner", fp.c_str());
                }
                if (has_attribute_name_in_nodes(nodes, oppo_camera_namespace, "MotionPhotoPrimaryPresentationTimestampUs")) {
                    std::string val = get_attribute_value_in_nodes(nodes, oppo_camera_namespace, "MotionPhotoPrimaryPresentationTimestampUs");
                    std::string fp = lpb::crypto::compute_xmp_property_fingerprint(oppo_camera_namespace, "MotionPhotoPrimaryPresentationTimestampUs", val);
                    add_residue(out_residues, "oppo-xmp-pts", LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO,
                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "MotionPhotoPrimaryPresentationTimestampUs", "MotionPhotoPrimaryPresentationTimestampUs", fp.c_str());
                }
                if (has_attribute_name_in_nodes(nodes, google_camera_namespace, "MotionPhotoPresentationTimestampUs")) {
                    std::string val = get_attribute_value_in_nodes(nodes, google_camera_namespace, "MotionPhotoPresentationTimestampUs");
                    std::string fp = lpb::crypto::compute_xmp_property_fingerprint(google_camera_namespace, "MotionPhotoPresentationTimestampUs", val);
                    add_residue(out_residues, "google-v2-xmp-pts", LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO,
                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "GCamera:MotionPhotoPresentationTimestampUs", "MotionPhotoPresentationTimestampUs", fp.c_str());
                }
                if (has_attribute_name_in_nodes(nodes, oppo_camera_namespace, "MotionPhotoEnable")) {
                    std::string val = get_attribute_value_in_nodes(nodes, oppo_camera_namespace, "MotionPhotoEnable");
                    std::string fp = lpb::crypto::compute_xmp_property_fingerprint(oppo_camera_namespace, "MotionPhotoEnable", val);
                    add_residue(out_residues, "oppo-xmp-enable", LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO,
                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "MotionPhotoEnable", "MotionPhotoEnable", fp.c_str());
                }
                container_directory_info oppo_dir;
                if (find_container_directory(nodes, oppo_dir, xmp_owner_description)) {
                    for (const auto& item : oppo_dir.items) {
                        if (item.semantic == "MotionPhoto") {
                            std::string fp = lpb::crypto::compute_xmp_container_item_fingerprint(
                                item.semantic, item.mime, item.length, item.padding, item.has_padding);
                            add_residue(out_residues, "oppo-container-item-motionphoto", LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO,
                                LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_CONTAINER_ITEM, "Item:Semantic=MotionPhoto", "MotionPhoto", fp.c_str());
                            break;
                        }
                    }
                }
                if (has_attribute_name_in_nodes(nodes, google_camera_namespace, "MotionPhoto")) {
                    std::string val = get_attribute_value_in_nodes(nodes, google_camera_namespace, "MotionPhoto");
                    std::string fp = lpb::crypto::compute_xmp_property_fingerprint(google_camera_namespace, "MotionPhoto", val);
                    add_residue(out_residues, "google-v2-xmp-motionphoto", LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO,
                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "GCamera:MotionPhoto", "MotionPhoto", fp.c_str());
                }
                if (has_attribute_name_in_nodes(nodes, google_camera_namespace, "MotionPhotoVersion")) {
                    std::string val = get_attribute_value_in_nodes(nodes, google_camera_namespace, "MotionPhotoVersion");
                    std::string fp = lpb::crypto::compute_xmp_property_fingerprint(google_camera_namespace, "MotionPhotoVersion", val);
                    add_residue(out_residues, "google-v2-xmp-version", LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO,
                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "GCamera:MotionPhotoVersion", "MotionPhotoVersion", fp.c_str());
                }
            }
            return LPB_RESULT_OK;
        }

        // Google Motion Photo V2 / Xiaomi
        const bool is_google_v2_candidate = has_attribute_name_in_nodes(nodes, google_camera_namespace, "MotionPhoto");
        if (is_google_v2_candidate) {
            set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED,
                LPB_INSPECTION_STAGE_PROTOCOL, LPB_CAPABILITY_GOOGLE_V2);

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

            // Resolve the authoritative Directory once before dispatching to
            // the HEIC or JPEG range validator. Both branches consume this
            // same parsed ownership graph.
            container_directory_info dir;
            if (!find_container_directory(nodes, dir, xmp_owner_description) || dir.items.empty()) {
                set_error(context, "Google Motion Photo V2 has MotionPhoto=1 but missing or malformed Container:Directory.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            if (img_cont == LPB_IMAGE_CONTAINER_HEIC) {
                size_t primary_end = 0;
                size_t video_offset = 0;
                size_t video_length = 0;
                isobmff_box_header mpvd_box{};
                if (!locate_google_heic_motion_payload(primary_data, primary_end,
                        video_offset, video_length, mpvd_box) ||
                    !is_valid_heic_container(context, primary_data.data(), primary_end) ||
                    !is_valid_isobmff_media_range(primary_data.data(), primary_data.size(),
                        video_offset, video_length)) {
                    set_error(context, "Google Motion Photo V2 HEIC mpvd or video payload is malformed.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }

                const auto& heic_primary_item = dir.items[0];
                const container_item_info* heic_motion_item = nullptr;
                size_t heic_primary_count = 0;
                size_t heic_motion_count = 0;
                for (const auto& item : dir.items) {
                    if (item.semantic == "Primary") {
                        ++heic_primary_count;
                    } else if (item.semantic == "MotionPhoto") {
                        ++heic_motion_count;
                        heic_motion_item = &item;
                    } else {
                        set_error(context, "Google Motion Photo V2 HEIC Container:Directory contains an unsupported item.");
                        return LPB_RESULT_INVALID_ARGUMENT;
                    }
                }
                if (heic_primary_count != 1 || heic_motion_count != 1 ||
                    dir.items.size() != 2 || heic_motion_item == nullptr ||
                    dir.items.back().semantic != "MotionPhoto" ||
                    heic_primary_item.mime != "image/heic" ||
                    heic_motion_item->mime != "video/quicktime" ||
                    heic_primary_item.malformed_length || heic_primary_item.malformed_padding ||
                    !heic_motion_item->has_length || heic_motion_item->malformed_length ||
                    heic_motion_item->length != video_length ||
                    (heic_motion_item->has_padding && heic_motion_item->padding != 0)) {
                    set_error(context, "Google Motion Photo V2 HEIC Container:Directory does not match mpvd ownership.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
                if (heic_primary_item.has_length && heic_primary_item.length != 0 &&
                    heic_primary_item.length != primary_end) {
                    set_error(context, "Google Motion Photo V2 HEIC Primary length does not match the HEIF range.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
                if (!heic_primary_item.has_padding ||
                    primary_end > video_offset ||
                    video_offset - primary_end != heic_primary_item.padding) {
                    set_error(context, "Google Motion Photo V2 HEIC Primary padding does not match the mpvd framing header.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
                if (!populate_heif_auxiliary(context, primary_data, out_facts)) {
                    set_heif_graph_failure_status(context, LPB_CAPABILITY_GOOGLE_V2);
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
                if (!bind_gainmap_from_heif_auxiliary(context, out_facts)) {
                    set_inspection_status(context, LPB_INSPECTION_FAILURE_AMBIGUOUS,
                        LPB_INSPECTION_STAGE_CONTAINER, LPB_CAPABILITY_GOOGLE_V2);
                    return LPB_RESULT_INVALID_ARGUMENT;
                }

                out_facts->protocol = LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2;
                out_facts->primary_image.file_range.offset = 0;
                out_facts->primary_image.file_range.length = primary_end;
                out_facts->motion_video.is_present = 1;
                out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MOV;
                out_facts->motion_video.file_range.offset = video_offset;
                out_facts->motion_video.file_range.length = video_length;
                out_facts->protocol_tail_range.offset = primary_end;
                out_facts->protocol_tail_range.length = video_offset - primary_end;
                int64_t cover_time = 0;
                if (get_global_attribute_i64(nodes, google_camera_namespace,
                        "MotionPhotoPresentationTimestampUs", cover_time) > 0) {
                    out_facts->timing.cover_timestamp_us = cover_time;
                }
                if (out_residues) {
                    if (has_attribute_name_in_nodes(nodes, google_camera_namespace, "MotionPhoto")) {
                        std::string val = get_attribute_value_in_nodes(nodes, google_camera_namespace, "MotionPhoto");
                        std::string fp = lpb::crypto::compute_xmp_property_fingerprint(
                            google_camera_namespace, "MotionPhoto", val);
                        add_residue(out_residues, "google-v2-xmp-motionphoto",
                            LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2, LPB_ARTIFACT_PRIMARY_IMAGE,
                            LPB_RESIDUE_XMP_PROPERTY, "GCamera:MotionPhoto", "MotionPhoto", fp.c_str());
                    }
                    if (has_attribute_name_in_nodes(nodes, google_camera_namespace, "MotionPhotoVersion")) {
                        std::string val = get_attribute_value_in_nodes(nodes, google_camera_namespace, "MotionPhotoVersion");
                        std::string fp = lpb::crypto::compute_xmp_property_fingerprint(
                            google_camera_namespace, "MotionPhotoVersion", val);
                        add_residue(out_residues, "google-v2-xmp-version",
                            LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2, LPB_ARTIFACT_PRIMARY_IMAGE,
                            LPB_RESIDUE_XMP_PROPERTY, "GCamera:MotionPhotoVersion", "MotionPhotoVersion", fp.c_str());
                    }
                    if (has_attribute_name_in_nodes(nodes, google_camera_namespace, "MotionPhotoPresentationTimestampUs")) {
                        std::string val = get_attribute_value_in_nodes(nodes, google_camera_namespace, "MotionPhotoPresentationTimestampUs");
                        std::string fp = lpb::crypto::compute_xmp_property_fingerprint(
                            google_camera_namespace, "MotionPhotoPresentationTimestampUs", val);
                        add_residue(out_residues, "google-v2-xmp-pts",
                            LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2, LPB_ARTIFACT_PRIMARY_IMAGE,
                            LPB_RESIDUE_XMP_PROPERTY, "GCamera:MotionPhotoPresentationTimestampUs",
                            "MotionPhotoPresentationTimestampUs", fp.c_str());
                    }
                    std::string fp_item = lpb::crypto::compute_xmp_container_item_fingerprint(
                        heic_motion_item->semantic, heic_motion_item->mime,
                        heic_motion_item->length, heic_motion_item->padding,
                        heic_motion_item->has_padding);
                    add_residue(out_residues, "google-v2-container-item-motionphoto",
                        LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2, LPB_ARTIFACT_PRIMARY_IMAGE,
                        LPB_RESIDUE_XMP_CONTAINER_ITEM, "Item:Semantic=MotionPhoto", "MotionPhoto", fp_item.c_str());
                    std::string fp_mpvd = lpb::crypto::compute_isobmff_box_fingerprint(
                        "mpvd", mpvd_box.size, primary_data.data() + mpvd_box.start, mpvd_box.size);
                    add_residue(out_residues, "google-v2-heic-box-mpvd",
                        LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2, LPB_ARTIFACT_PRIMARY_IMAGE,
                        LPB_RESIDUE_ISOBMFF_BOX, "mpvd", "mpvd", fp_mpvd.c_str(),
                        LPB_COORD_STRUCTURED_SELECTOR, LPB_REMOVAL_REBUILD_CONTAINER, 1);
                }
                return LPB_RESULT_OK;
            }
            if (img_cont != LPB_IMAGE_CONTAINER_JPEG) {
                set_error(context, "Google Motion Photo V2 requires JPEG container.");
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
                if (!publish_gainmap_auxiliary(out_facts, primary_data, LPB_IMAGE_CONTAINER_JPEG,
                        out_facts->gain_map.file_range)) {
                    set_error(context, "Google Motion Photo GainMap could not be bound to an auxiliary identity.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
            }

            out_facts->primary_image.file_range.offset = 0;
            out_facts->primary_image.file_range.length = pri_len;

            int64_t cover_time = 0;
            if (get_global_attribute_i64(nodes, google_camera_namespace, "MotionPhotoPresentationTimestampUs", cover_time) > 0) {
                out_facts->timing.cover_timestamp_us = cover_time;
            }

            if (out_residues) {
                if (has_attribute_name_in_nodes(nodes, google_camera_namespace, "MotionPhoto")) {
                    std::string val = get_attribute_value_in_nodes(nodes, google_camera_namespace, "MotionPhoto");
                    std::string fp = lpb::crypto::compute_xmp_property_fingerprint(google_camera_namespace, "MotionPhoto", val);
                    add_residue(out_residues, "google-v2-xmp-motionphoto", LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2,
                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "GCamera:MotionPhoto", "MotionPhoto", fp.c_str());
                }
                if (has_attribute_name_in_nodes(nodes, google_camera_namespace, "MotionPhotoVersion")) {
                    std::string val = get_attribute_value_in_nodes(nodes, google_camera_namespace, "MotionPhotoVersion");
                    std::string fp = lpb::crypto::compute_xmp_property_fingerprint(google_camera_namespace, "MotionPhotoVersion", val);
                    add_residue(out_residues, "google-v2-xmp-version", LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2,
                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "GCamera:MotionPhotoVersion", "MotionPhotoVersion", fp.c_str());
                }
                if (has_attribute_name_in_nodes(nodes, google_camera_namespace, "MotionPhotoPresentationTimestampUs")) {
                    std::string val = get_attribute_value_in_nodes(nodes, google_camera_namespace, "MotionPhotoPresentationTimestampUs");
                    std::string fp = lpb::crypto::compute_xmp_property_fingerprint(google_camera_namespace, "MotionPhotoPresentationTimestampUs", val);
                    add_residue(out_residues, "google-v2-xmp-pts", LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2,
                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "GCamera:MotionPhotoPresentationTimestampUs", "MotionPhotoPresentationTimestampUs", fp.c_str());
                }
                std::string fp_item = lpb::crypto::compute_xmp_container_item_fingerprint(
                    motion_item->semantic, motion_item->mime, motion_item->length, motion_item->padding, motion_item->has_padding);
                add_residue(out_residues, "google-v2-container-item-motionphoto", LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2,
                    LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_CONTAINER_ITEM, "Item:Semantic=MotionPhoto", "MotionPhoto", fp_item.c_str());
            }
            return LPB_RESULT_OK;
        }

        // Google MicroVideo V1
        const bool is_google_v1_candidate = has_attribute_name_in_nodes(nodes, google_camera_namespace, "MicroVideo") ||
            has_attribute_name_in_nodes(nodes, google_camera_namespace, "MicroVideoOffset");
        if (is_google_v1_candidate) {
            set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED,
                LPB_INSPECTION_STAGE_PROTOCOL, LPB_CAPABILITY_GOOGLE_V1);
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
            // The XMP offset identifies the video payload, not an opaque
            // extension of the JPEG. Keep the primary artifact at the
            // structurally validated JPEG boundary and account for any
            // report-observed inter-resource padding explicitly.
            if (!has_jpeg_end || jpeg_end > vid_offset ||
                !is_valid_jpeg_media_range(primary_data.data(), primary_data.size(), 0, jpeg_end)) {
                set_error(context, "Google MicroVideo V1 primary JPEG range is malformed or overlaps the video.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            out_facts->protocol = LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1;
            out_facts->motion_video.is_present = 1;
            out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
            out_facts->motion_video.file_range.offset = vid_offset;
            out_facts->motion_video.file_range.length = mv_offset;
            out_facts->primary_image.file_range.offset = 0;
            out_facts->primary_image.file_range.length = jpeg_end;
            if (jpeg_end < vid_offset) {
                out_facts->protocol_tail_range.offset = jpeg_end;
                out_facts->protocol_tail_range.length = vid_offset - jpeg_end;
            }
            int64_t cover_time = 0;
            if (get_global_attribute_i64(nodes, google_camera_namespace, "MicroVideoPresentationTimestampUs", cover_time) > 0) {
                out_facts->timing.cover_timestamp_us = cover_time;
            }

            if (out_residues) {
                std::string val_mv = get_attribute_value_in_nodes(nodes, google_camera_namespace, "MicroVideo");
                std::string fp_mv = lpb::crypto::compute_xmp_property_fingerprint(google_camera_namespace, "MicroVideo", val_mv);
                add_residue(out_residues, "google-v1-xmp-microvideo", LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1,
                    LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "GCamera:MicroVideo", "MicroVideo", fp_mv.c_str());

                std::string val_ver = get_attribute_value_in_nodes(nodes, google_camera_namespace, "MicroVideoVersion");
                std::string fp_ver = lpb::crypto::compute_xmp_property_fingerprint(google_camera_namespace, "MicroVideoVersion", val_ver);
                add_residue(out_residues, "google-v1-xmp-version", LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1,
                    LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "GCamera:MicroVideoVersion", "MicroVideoVersion", fp_ver.c_str());

                std::string val_off = get_attribute_value_in_nodes(nodes, google_camera_namespace, "MicroVideoOffset");
                std::string fp_off = lpb::crypto::compute_xmp_property_fingerprint(google_camera_namespace, "MicroVideoOffset", val_off);
                add_residue(out_residues, "google-v1-xmp-offset", LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1,
                    LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "GCamera:MicroVideoOffset", "MicroVideoOffset", fp_off.c_str());

                if (has_attribute_name_in_nodes(nodes, google_camera_namespace, "MicroVideoPresentationTimestampUs")) {
                    std::string val_pts = get_attribute_value_in_nodes(nodes, google_camera_namespace, "MicroVideoPresentationTimestampUs");
                    std::string fp_pts = lpb::crypto::compute_xmp_property_fingerprint(google_camera_namespace, "MicroVideoPresentationTimestampUs", val_pts);
                    add_residue(out_residues, "google-v1-xmp-pts", LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1,
                        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_XMP_PROPERTY, "GCamera:MicroVideoPresentationTimestampUs", "MicroVideoPresentationTimestampUs", fp_pts.c_str());
                }
            }
            return LPB_RESULT_OK;
        }
    }

    // A cleaned neutral JPEG may intentionally retain an auxiliary GainMap
    // JPEG after the primary EOI.  This is not an unexplained protocol tail:
    // accept it only when the authoritative XMP Container Directory has
    // exactly Primary + GainMap, with an exact non-overlapping range.
    if (img_cont == LPB_IMAGE_CONTAINER_JPEG && xmp_parsed && !nodes.empty()) {
        container_directory_info neutral_dir;
        if (find_container_directory(nodes, neutral_dir, xmp_owner_description) && neutral_dir.items.size() == 2) {
            const container_item_info* primary_item = nullptr;
            const container_item_info* gainmap_item = nullptr;
            bool neutral_items_valid = true;
            for (const auto& item : neutral_dir.items) {
                if (item.semantic == "Primary" && primary_item == nullptr) primary_item = &item;
                else if (item.semantic == "GainMap" && gainmap_item == nullptr) gainmap_item = &item;
                else neutral_items_valid = false;
            }
            if (neutral_items_valid && primary_item != nullptr && gainmap_item != nullptr &&
                gainmap_item->mime == "image/jpeg" && gainmap_item->has_length &&
                !gainmap_item->malformed_length && gainmap_item->length > 0 &&
                gainmap_item->length < primary_size) {
                const uint64_t gainmap_offset = primary_size - gainmap_item->length;
                const bool has_retained_sef = gainmap_offset >= jpeg_end &&
                    (gainmap_offset == jpeg_end || is_valid_non_motion_sef_range(
                        primary_data, jpeg_end, gainmap_offset - jpeg_end));
                if (has_retained_sef &&
                    is_valid_jpeg_media_range(primary_data.data(), primary_data.size(),
                        gainmap_offset, gainmap_item->length)) {
                    const uint64_t primary_length = primary_item->has_length && primary_item->length > 0
                        ? primary_item->length : jpeg_end;
                    if (primary_length <= gainmap_offset &&
                        is_valid_jpeg_media_range(primary_data.data(), primary_data.size(), 0, primary_length)) {
                        out_facts->protocol = LPB_SOURCE_PROTOCOL_NON_LIVE;
                        out_facts->primary_image.is_present = 1;
                        out_facts->primary_image.file_range.offset = 0;
                        out_facts->primary_image.file_range.length = primary_length;
                        out_facts->gain_map.is_present = 1;
                        out_facts->gain_map.container = LPB_IMAGE_CONTAINER_JPEG;
                        out_facts->gain_map.file_range.offset = gainmap_offset;
                        out_facts->gain_map.file_range.length = gainmap_item->length;
                        if (gainmap_offset > jpeg_end) {
                            out_facts->protocol_tail_range.offset = jpeg_end;
                            out_facts->protocol_tail_range.length = gainmap_offset - jpeg_end;
                        }
                        if (!publish_gainmap_auxiliary(out_facts, primary_data, LPB_IMAGE_CONTAINER_JPEG,
                                out_facts->gain_map.file_range)) {
                            set_error(context, "Neutral GainMap could not be bound to an auxiliary identity.");
                            set_inspection_status(context, LPB_INSPECTION_FAILURE_AMBIGUOUS,
                                LPB_INSPECTION_STAGE_CONTAINER);
                            return LPB_RESULT_INVALID_ARGUMENT;
                        }
                        return LPB_RESULT_OK;
                    }
                }
            }
        }
    }

    // 5. Single-member Apple CID or Vivo ID check
    std::string single_vivo_id;
    if (img_cont != LPB_IMAGE_CONTAINER_UNKNOWN) {
        std::string apple_cid;
        bool has_mn_conflict = false;
        if (extract_apple_cid_from_image(context, primary_data, img_cont, apple_cid, has_mn_conflict)) {
            strncpy_s(out_facts->pairing_identifier, apple_cid.c_str(), _TRUNCATE);
        } else if (has_mn_conflict) {
            set_error(context, "Apple image contains conflicting ContentIdentifiers in MakerNote.");
            set_inspection_status(context, LPB_INSPECTION_FAILURE_AMBIGUOUS,
                LPB_INSPECTION_STAGE_PAIRING, LPB_CAPABILITY_APPLE);
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        if (has_jpeg_end && extract_vivo_id_from_image(primary_data, jpeg_end, single_vivo_id)) {
            strncpy_s(out_facts->pairing_identifier, single_vivo_id.c_str(), _TRUNCATE);
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
            set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED, LPB_INSPECTION_STAGE_CONTAINER);
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        // A JPEG followed by unexplained bytes is not an ordinary image. Do
        // not silently absorb an unrecognised payload into the primary range:
        // protocol-specific branches above must prove and claim every tail.
        if (jpeg_end != primary_size) {
            set_error(context, single_vivo_id.empty()
                ? "JPEG contains unexplained trailing bytes; source protocol is ambiguous."
                : "vivo legacy image marker has no matching video identifier; pairing is ambiguous.");
            set_inspection_status(context, LPB_INSPECTION_FAILURE_AMBIGUOUS,
                LPB_INSPECTION_STAGE_PAIRING,
                single_vivo_id.empty() ? 0 : LPB_CAPABILITY_VIVO_LEGACY);
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
        if (!is_valid_heic_container(context, primary_data.data(), primary_data.size())) {
            set_error(context, "Primary file is a malformed HEIC container.");
            set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED, LPB_INSPECTION_STAGE_CONTAINER);
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (!populate_heif_auxiliary(context, primary_data, out_facts)) {
            set_heif_graph_failure_status(context, 0);
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (!bind_gainmap_from_heif_auxiliary(context, out_facts)) {
            set_inspection_status(context, LPB_INSPECTION_FAILURE_AMBIGUOUS,
                LPB_INSPECTION_STAGE_CONTAINER);
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
                set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED, LPB_INSPECTION_STAGE_CONTAINER);
                return LPB_RESULT_INVALID_ARGUMENT;
            }
        } else if (vid_cont == LPB_VIDEO_CONTAINER_MOV) {
            if (!is_valid_mov_container(primary_data.data(), primary_data.size())) {
                set_error(context, "Primary file is a malformed MOV video.");
                set_inspection_status(context, LPB_INSPECTION_FAILURE_MALFORMED, LPB_INSPECTION_STAGE_CONTAINER);
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
    set_inspection_status(context, LPB_INSPECTION_FAILURE_UNSUPPORTED, LPB_INSPECTION_STAGE_CONTAINER);
    out_facts->protocol = LPB_SOURCE_PROTOCOL_UNKNOWN;
    return LPB_RESULT_INVALID_ARGUMENT;
}

lpb_result inspect_source_with_plan(
    lpb_context* context,
    const char* primary_path,
    const char* secondary_path,
    lpb_source_media_facts* out_facts,
    lpb_extraction_plan** out_plan,
    std::vector<lpb_confirmed_residue>* out_residues,
    uint64_t* out_generation) noexcept
{
    if (context == nullptr || out_facts == nullptr || out_plan == nullptr)
    {
        set_error(context, "Context, output facts, and output extraction plan are required.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    *out_plan = nullptr;
    if (out_generation != nullptr)
    {
        *out_generation = 0;
    }
    std::vector<lpb_confirmed_residue> residues;
    const lpb_result inspect_result = inspect_source(
        context, primary_path, secondary_path, out_facts,
        out_residues == nullptr ? nullptr : &residues);
    if (inspect_result != LPB_RESULT_OK)
    {
        return inspect_result;
    }

    if (out_residues != nullptr)
    {
        *out_residues = residues;
    }

    lpb_file_identity primary_identity{};
    std::wstring primary_final_path;
    if (!capture_file_identity(primary_path, primary_identity, primary_final_path))
    {
        set_error(context, "[SourceRangeUnreadable] Inspector could not capture primary source identity.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    const bool has_secondary = secondary_path != nullptr && secondary_path[0] != '\0';
    lpb_file_identity secondary_identity{};
    std::wstring secondary_final_path;
    if (has_secondary && !capture_file_identity(secondary_path, secondary_identity, secondary_final_path))
    {
        set_error(context, "[SourceRangeUnreadable] Inspector could not capture secondary source identity.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    try
    {
        lpb_extraction_plan_record plan{};
        plan.owner_context = context;
        plan.abi_version = LPB_NATIVE_ABI_VERSION;
        plan.plan_version = 1;
        plan.facts = *out_facts;
        plan.primary_identity = primary_identity;
        plan.secondary_identity = secondary_identity;
        plan.has_secondary = has_secondary;
        plan.primary_final_path = std::move(primary_final_path);
        plan.secondary_final_path = std::move(secondary_final_path);
        if (!generate_plan_token(context, plan.token))
        {
            set_error(context, "[InternalError] Failed to generate an opaque extraction plan token.");
            return LPB_RESULT_INTERNAL_ERROR;
        }
        std::scoped_lock lock(context->plan_mutex);
        if (context->next_plan_generation == 0)
        {
            set_error(context, "[InternalError] Extraction plan generation space is exhausted.");
            return LPB_RESULT_INTERNAL_ERROR;
        }
        const uint64_t generation = plan.generation = context->next_plan_generation++;
        context->extraction_plans.push_back(std::move(plan));
#if defined(LPB_NATIVE_TEST_HARNESS)
        ++context->test_issued;
#endif
        *out_plan = plan_handle_from_token(context->extraction_plans.back().token);
        if (out_generation != nullptr)
        {
            *out_generation = generation;
        }
        return LPB_RESULT_OK;
    }
    catch (const std::exception& ex)
    {
        set_error(context, ex.what());
        return LPB_RESULT_INTERNAL_ERROR;
    }
    catch (...)
    {
        set_error(context, "Failed to allocate an extraction authority plan.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
}

} // namespace lpb::media
