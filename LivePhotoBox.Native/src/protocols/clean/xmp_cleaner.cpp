#include "xmp_cleaner.h"

#include <algorithm>
#include <cctype>
#include <cstring>
#include <string_view>
#include <utility>
#include <vector>

namespace lpb::protocols::clean {

namespace {

struct attribute_span
{
    size_t start{};
    size_t end{};
    std::string_view name{};
    std::string_view value{};
};

using namespace_binding = std::pair<std::string_view, std::string_view>;

static constexpr std::string_view google_camera_namespace = "http://ns.google.com/photos/1.0/camera/";
static constexpr std::string_view google_container_namespace = "http://ns.google.com/photos/1.0/container/";
static constexpr std::string_view google_item_namespace = "http://ns.google.com/photos/1.0/container/item/";
static constexpr std::string_view rdf_namespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
static constexpr std::string_view oppo_camera_namespace = "http://ns.oplus.com/photos/1.0/camera/";
static constexpr std::string_view vivo_camera_namespace = "http://ns.vivo.com/photos/1.0/camera/";

struct parsed_element
{
    size_t start{};
    size_t start_tag_end{};
    size_t end{};
    std::string_view name{};
    std::vector<attribute_span> attributes{};
    std::vector<namespace_binding> bindings{};
};

static bool is_name_char(char c) noexcept
{
    return std::isalnum(static_cast<unsigned char>(c)) != 0 || c == ':' || c == '_' || c == '-';
}

static std::string_view local_name(std::string_view name) noexcept
{
    const size_t colon = name.rfind(':');
    return colon == std::string_view::npos ? name : name.substr(colon + 1);
}

static bool equals_icase(std::string_view a, std::string_view b) noexcept
{
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i)
    {
        if (std::tolower(static_cast<unsigned char>(a[i])) !=
            std::tolower(static_cast<unsigned char>(b[i]))) return false;
    }
    return true;
}

static bool find_tag_end(std::string_view xml, size_t start, size_t& end) noexcept
{
    char quote = 0;
    for (size_t i = start; i < xml.size(); ++i)
    {
        const char c = xml[i];
        if (quote != 0)
        {
            if (c == quote) quote = 0;
        }
        else if (c == '\'' || c == '"')
        {
            quote = c;
        }
        else if (c == '>')
        {
            end = i + 1;
            return true;
        }
    }
    return false;
}

static bool parse_start_tag(std::string_view xml, size_t start, size_t end,
    std::string_view& tag_name, std::vector<attribute_span>& attributes) noexcept
{
    if (start >= end || xml[start] != '<' || start + 1 >= end) return false;
    size_t p = start + 1;
    if (xml[p] == '/' || xml[p] == '!' || xml[p] == '?') return false;
    const size_t name_start = p;
    while (p < end && is_name_char(xml[p])) ++p;
    if (p == name_start) return false;
    tag_name = xml.substr(name_start, p - name_start);

    while (p < end)
    {
        while (p < end && (std::isspace(static_cast<unsigned char>(xml[p])) || xml[p] == '/')) ++p;
        if (p >= end || xml[p] == '>') break;
        const size_t attr_start = p;
        while (p < end && is_name_char(xml[p])) ++p;
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
        ++p;
        attributes.push_back({ attr_start, p, attr_name, xml.substr(value_start, (p - 1) - value_start) });
    }
    return true;
}

static void set_namespace_binding(std::vector<namespace_binding>& bindings,
    std::string_view prefix, std::string_view uri)
{
    for (auto& binding : bindings)
    {
        if (binding.first == prefix)
        {
            binding.second = uri;
            return;
        }
    }
    bindings.emplace_back(prefix, uri);
}

static void collect_namespace_bindings(const std::vector<attribute_span>& attributes,
    std::vector<namespace_binding>& bindings)
{
    for (const auto& attribute : attributes)
    {
        if (attribute.name == "xmlns")
        {
            set_namespace_binding(bindings, {}, attribute.value);
        }
        else if (attribute.name.size() > 6 && attribute.name.substr(0, 6) == "xmlns:")
        {
            set_namespace_binding(bindings, attribute.name.substr(6), attribute.value);
        }
    }
}

static bool parse_xml_elements(std::string_view xml, std::vector<parsed_element>& elements)
{
    std::vector<size_t> open_elements;
    size_t p = 0;
    while ((p = xml.find('<', p)) != std::string_view::npos)
    {
        if (xml.substr(p, 4) == "<!--")
        {
            const size_t comment_end = xml.find("-->", p + 4);
            if (comment_end == std::string_view::npos) return false;
            p = comment_end + 3;
            continue;
        }

        size_t tag_end = 0;
        if (!find_tag_end(xml, p, tag_end)) return false;
        if (p + 1 < xml.size() && (xml[p + 1] == '!' || xml[p + 1] == '?'))
        {
            p = tag_end;
            continue;
        }

        if (p + 1 < xml.size() && xml[p + 1] == '/')
        {
            if (open_elements.empty()) return false;
            parsed_element& element = elements[open_elements.back()];
            element.end = tag_end;
            open_elements.pop_back();
            p = tag_end;
            continue;
        }

        std::string_view tag_name;
        std::vector<attribute_span> attributes;
        if (!parse_start_tag(xml, p, tag_end, tag_name, attributes))
        {
            p = tag_end;
            continue;
        }

        std::vector<namespace_binding> bindings = open_elements.empty()
            ? std::vector<namespace_binding>{}
            : elements[open_elements.back()].bindings;
        collect_namespace_bindings(attributes, bindings);

        const size_t element_index = elements.size();
        elements.push_back({ p, tag_end, tag_end, tag_name, std::move(attributes), std::move(bindings) });
        const bool self_closing = tag_end >= 2 && xml[tag_end - 2] == '/';
        if (!self_closing) open_elements.push_back(element_index);
        p = tag_end;
    }
    return open_elements.empty();
}

static std::string_view namespace_uri_for_name(std::string_view name,
    const std::vector<namespace_binding>& bindings, bool attribute_name) noexcept
{
    const size_t colon = name.find(':');
    if (colon == std::string_view::npos && attribute_name) return {};
    const std::string_view prefix = colon == std::string_view::npos ? std::string_view{} : name.substr(0, colon);
    for (auto it = bindings.rbegin(); it != bindings.rend(); ++it)
    {
        if (it->first == prefix) return it->second;
    }
    return {};
}

static bool is_google_camera_attribute(std::string_view local, lpb_source_protocol protocol) noexcept
{
    if (protocol == LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1)
    {
        return equals_icase(local, "MicroVideo") ||
            equals_icase(local, "MicroVideoVersion") ||
            equals_icase(local, "MicroVideoOffset") ||
            equals_icase(local, "MicroVideoPresentationTimestampUs");
    }

    return equals_icase(local, "MotionPhoto") ||
        equals_icase(local, "MotionPhotoVersion") ||
        equals_icase(local, "MotionPhotoPresentationTimestampUs") ||
        equals_icase(local, "MotionPhotoPrimaryPresentationTimestampUs") ||
        equals_icase(local, "MotionPhotoOwner") ||
        equals_icase(local, "MotionPhotoEnable");
}

static bool is_container_protocol(lpb_source_protocol protocol) noexcept
{
    return protocol == LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2 ||
        protocol == LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO ||
        protocol == LPB_SOURCE_PROTOCOL_VIVO_X300 ||
        protocol == LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG ||
        protocol == LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC;
}

static bool is_protocol_attribute(std::string_view name,
    std::string_view uri, lpb_source_protocol protocol) noexcept
{
    const std::string_view local = local_name(name);
    if (uri == google_camera_namespace &&
        (protocol == LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1 ||
         protocol == LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2 ||
         protocol == LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO ||
         protocol == LPB_SOURCE_PROTOCOL_VIVO_X300 ||
         protocol == LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG ||
         protocol == LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC))
    {
        return is_google_camera_attribute(local, protocol);
    }

    if (protocol == LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO && uri == oppo_camera_namespace)
    {
        return equals_icase(local, "OLivePhotoVersion") ||
            equals_icase(local, "VideoLength");
    }

    if ((protocol == LPB_SOURCE_PROTOCOL_VIVO_X300 ||
         protocol == LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL) && uri == vivo_camera_namespace)
    {
        return equals_icase(local, "VMotionPhotoVersion") ||
            equals_icase(local, "VMotionPhotoSource") ||
            equals_icase(local, "VMotionPhotoFlags") ||
            equals_icase(local, "VMediaKitVersion");
    }

    // Apple Live Photo pairing is stored in MakerNote / QuickTime metadata,
    // not an arbitrary XMP ContentIdentifier attribute.
    return false;
}

static bool item_is_motion(const parsed_element& element, lpb_source_protocol protocol) noexcept
{
    if (!equals_icase(local_name(element.name), "Item") ||
        !is_container_protocol(protocol) ||
        namespace_uri_for_name(element.name, element.bindings, false) != google_container_namespace) return false;

    for (const auto& attr : element.attributes)
    {
        if (namespace_uri_for_name(attr.name, element.bindings, true) != google_item_namespace) continue;
        if (equals_icase(local_name(attr.name), "Semantic") &&
            equals_icase(attr.value, "MotionPhoto")) return true;
    }
    return false;
}

static void add_fact(std::vector<lpb_removed_protocol_fact>& out_facts,
    const char* proto, const char* component, const char* description)
{
    lpb_removed_protocol_fact fact{};
    fact.struct_size = sizeof(lpb_removed_protocol_fact);
    strncpy_s(fact.protocol_name, proto, _TRUNCATE);
    strncpy_s(fact.component, component, _TRUNCATE);
    strncpy_s(fact.description, description, _TRUNCATE);
    out_facts.push_back(fact);
}

static bool find_motion_ranges(std::string_view xml,
    lpb_source_protocol protocol,
    std::vector<std::pair<size_t, size_t>>& ranges) noexcept
{
    std::vector<parsed_element> elements;
    if (!parse_xml_elements(xml, elements)) return false;

    for (const auto& element : elements)
    {
        if (!item_is_motion(element, protocol)) continue;

        size_t start = element.start;
        size_t end = element.end;
        size_t best_wrapper_size = static_cast<size_t>(-1);
        for (const auto& wrapper : elements)
        {
            if (!equals_icase(local_name(wrapper.name), "li") ||
                namespace_uri_for_name(wrapper.name, wrapper.bindings, false) != rdf_namespace ||
                wrapper.start >= element.start || wrapper.end < element.end) continue;
            const size_t wrapper_size = wrapper.end - wrapper.start;
            if (wrapper_size < best_wrapper_size)
            {
                best_wrapper_size = wrapper_size;
                start = wrapper.start;
                end = wrapper.end;
            }
        }
        ranges.push_back({ start, end });
    }
    return true;
}

} // namespace

bool clean_xmp_metadata(
    const std::string& input_xmp,
    lpb_source_protocol protocol,
    std::string& output_xmp,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    if (input_xmp.empty()) return false;
    const std::string_view xml(input_xmp);

    std::vector<std::pair<size_t, size_t>> motion_ranges;
    if (!find_motion_ranges(xml, protocol, motion_ranges)) return false;

    // Rebuild start tags while dropping only exact protocol attributes. Values
    // and text nodes are copied byte-for-byte, so words such as MotionPhoto in
    // a normal caption cannot trigger a destructive edit.
    std::string cleaned;
    cleaned.reserve(xml.size());
    std::vector<parsed_element> elements;
    if (!parse_xml_elements(xml, elements)) return false;
    size_t p = 0;
    bool removed_attribute = false;
    for (const auto& element : elements)
    {
        cleaned.append(xml.substr(p, element.start - p));
        size_t cursor = element.start;
        for (const auto& attr : element.attributes)
        {
            if (is_protocol_attribute(attr.name,
                namespace_uri_for_name(attr.name, element.bindings, true), protocol))
            {
                cleaned.append(xml.substr(cursor, attr.start - cursor));
                cursor = attr.end;
                removed_attribute = true;
            }
        }
        cleaned.append(xml.substr(cursor, element.start_tag_end - cursor));
        p = element.start_tag_end;
    }
    cleaned.append(xml.substr(p));

    // Attribute removal changes offsets. Re-discover motion item ranges on the
    // rebuilt XML before applying them; never use stale string-search offsets.
    if (!motion_ranges.empty())
    {
        std::vector<std::pair<size_t, size_t>> final_ranges;
        if (!find_motion_ranges(cleaned, protocol, final_ranges)) return false;
        std::sort(final_ranges.begin(), final_ranges.end());
        final_ranges.erase(std::unique(final_ranges.begin(), final_ranges.end()), final_ranges.end());

        std::string without_items;
        const std::string_view current(cleaned);
        size_t cursor = 0;
        for (const auto& range : final_ranges)
        {
            if (range.first < cursor || range.second > current.size()) return false;
            without_items.append(current.substr(cursor, range.first - cursor));
            cursor = range.second;
        }
        without_items.append(current.substr(cursor));
        cleaned.swap(without_items);
    }

    if (!removed_attribute && motion_ranges.empty()) return false;
    if (cleaned == input_xmp) return false;
    output_xmp = std::move(cleaned);
    if (removed_attribute) add_fact(out_facts, "Source protocol", "XMP attributes", "Removed validated Live/Motion Photo attributes");
    if (!motion_ranges.empty()) add_fact(out_facts, "Source protocol", "XMP Container Directory", "Removed validated motion-video item");
    return true;
}

} // namespace lpb::protocols::clean
