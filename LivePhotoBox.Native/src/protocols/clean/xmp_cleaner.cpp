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
static constexpr std::string_view oppo_camera_namespace = "http://ns.oplus.com/photos/1.0/camera/";
static constexpr std::string_view vivo_camera_namespace = "http://ns.vivo.com/photos/1.0/camera/";

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

static bool item_is_motion(std::string_view xml, size_t tag_start, size_t tag_end,
    const std::vector<namespace_binding>& bindings, lpb_source_protocol protocol) noexcept
{
    std::string_view tag_name;
    std::vector<attribute_span> attrs;
    if (!parse_start_tag(xml, tag_start, tag_end, tag_name, attrs) ||
        !equals_icase(local_name(tag_name), "Item") ||
        !is_container_protocol(protocol) ||
        namespace_uri_for_name(tag_name, bindings, false) != google_container_namespace) return false;

    for (const auto& attr : attrs)
    {
        if (namespace_uri_for_name(attr.name, bindings, true) != google_item_namespace) continue;
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
    const std::vector<namespace_binding>& bindings,
    std::vector<std::pair<size_t, size_t>>& ranges) noexcept
{
    size_t p = 0;
    while ((p = xml.find('<', p)) != std::string_view::npos)
    {
        size_t tag_end = 0;
        if (!find_tag_end(xml, p, tag_end)) return false;
        std::string_view tag_name;
        std::vector<attribute_span> attrs;
        if (parse_start_tag(xml, p, tag_end, tag_name, attrs) &&
            equals_icase(local_name(tag_name), "Item") &&
            item_is_motion(xml, p, tag_end, bindings, protocol))
        {
            size_t start = p;
            size_t end = tag_end;
            const size_t li_start = xml.rfind("<rdf:li", p);
            const size_t li_close = li_start == std::string_view::npos
                ? std::string_view::npos : xml.find("</rdf:li>", p);
            if (li_start != std::string_view::npos && li_close != std::string_view::npos && li_start < p)
            {
                start = li_start;
                end = li_close + std::strlen("</rdf:li>");
            }
            ranges.push_back({ start, end });
        }
        p = tag_end;
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

    std::vector<namespace_binding> bindings;
    size_t scan = 0;
    while ((scan = xml.find('<', scan)) != std::string_view::npos)
    {
        size_t tag_end = 0;
        if (!find_tag_end(xml, scan, tag_end)) return false;
        std::string_view tag_name;
        std::vector<attribute_span> attrs;
        if (parse_start_tag(xml, scan, tag_end, tag_name, attrs))
            collect_namespace_bindings(attrs, bindings);
        scan = tag_end;
    }

    std::vector<std::pair<size_t, size_t>> motion_ranges;
    if (!find_motion_ranges(xml, protocol, bindings, motion_ranges)) return false;

    // Rebuild start tags while dropping only exact protocol attributes. Values
    // and text nodes are copied byte-for-byte, so words such as MotionPhoto in
    // a normal caption cannot trigger a destructive edit.
    std::string cleaned;
    cleaned.reserve(xml.size());
    size_t p = 0;
    bool removed_attribute = false;
    while (p < xml.size())
    {
        const size_t tag_start = xml.find('<', p);
        if (tag_start == std::string_view::npos)
        {
            cleaned.append(xml.substr(p));
            break;
        }
        cleaned.append(xml.substr(p, tag_start - p));
        size_t tag_end = 0;
        if (!find_tag_end(xml, tag_start, tag_end)) return false;

        std::string_view tag_name;
        std::vector<attribute_span> attrs;
        if (!parse_start_tag(xml, tag_start, tag_end, tag_name, attrs))
        {
            cleaned.append(xml.substr(tag_start, tag_end - tag_start));
        }
        else
        {
            size_t cursor = tag_start;
            for (const auto& attr : attrs)
            {
                if (is_protocol_attribute(attr.name,
                    namespace_uri_for_name(attr.name, bindings, true), protocol))
                {
                    cleaned.append(xml.substr(cursor, attr.start - cursor));
                    cursor = attr.end;
                    removed_attribute = true;
                }
            }
            cleaned.append(xml.substr(cursor, tag_end - cursor));
        }
        p = tag_end;
    }

    // Attribute removal changes offsets. Re-discover motion item ranges on the
    // rebuilt XML before applying them; never use stale string-search offsets.
    if (!motion_ranges.empty())
    {
        std::vector<std::pair<size_t, size_t>> final_ranges;
        std::vector<namespace_binding> cleaned_bindings;
        size_t cleaned_scan = 0;
        while ((cleaned_scan = std::string_view(cleaned).find('<', cleaned_scan)) != std::string_view::npos)
        {
            size_t tag_end = 0;
            if (!find_tag_end(cleaned, cleaned_scan, tag_end)) return false;
            std::string_view tag_name;
            std::vector<attribute_span> attrs;
            if (parse_start_tag(cleaned, cleaned_scan, tag_end, tag_name, attrs))
                collect_namespace_bindings(attrs, cleaned_bindings);
            cleaned_scan = tag_end;
        }
        if (!find_motion_ranges(cleaned, protocol, cleaned_bindings, final_ranges)) return false;
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
