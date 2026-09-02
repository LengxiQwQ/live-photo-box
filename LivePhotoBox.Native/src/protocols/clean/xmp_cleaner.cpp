#include "xmp_cleaner.h"

#include <algorithm>
#include <cctype>
#include <cstring>
#include <string_view>
#include <vector>

namespace lpb::protocols::clean {

namespace {

struct attribute_span
{
    size_t start{};
    size_t end{};
    std::string_view name{};
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

static bool is_protocol_attribute(std::string_view name, lpb_source_protocol protocol) noexcept
{
    const std::string_view local = local_name(name);
    static constexpr std::string_view common[] = {
        "MotionPhoto", "MotionPhotoVersion", "MotionPhotoPresentationTimestampUs",
        "MotionPhotoPrimaryPresentationTimestampUs", "MotionPhotoOwner", "MotionPhotoEnable",
        "MicroVideo", "MicroVideoVersion", "MicroVideoOffset", "MicroVideoPresentationTimestampUs",
        "OLivePhotoVersion", "VideoLength", "VMotionPhotoVersion", "VMotionPhotoSource",
        "VMotionPhotoFlags", "VMediaKitVersion"
    };
    for (const auto candidate : common)
    {
        if (equals_icase(local, candidate)) return true;
    }
    if (protocol == LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO)
    {
        return equals_icase(local, "ContentIdentifier") || equals_icase(local, "PhotoIdentifier");
    }
    return false;
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
        while (p < end && xml[p] != quote) ++p;
        if (p >= end) return false;
        ++p;
        attributes.push_back({ attr_start, p, attr_name });
    }
    return true;
}

static bool item_is_motion(std::string_view xml, size_t tag_start, size_t tag_end) noexcept
{
    std::string_view tag_name;
    std::vector<attribute_span> attrs;
    if (!parse_start_tag(xml, tag_start, tag_end, tag_name, attrs) ||
        !equals_icase(local_name(tag_name), "Item")) return false;

    for (const auto& attr : attrs)
    {
        const std::string_view local = local_name(attr.name);
        if (!equals_icase(local, "Mime") && !equals_icase(local, "Semantic")) continue;
        size_t p = attr.start;
        while (p < attr.end && xml[p] != '=') ++p;
        if (p >= attr.end) continue;
        ++p;
        while (p < attr.end && std::isspace(static_cast<unsigned char>(xml[p]))) ++p;
        if (p >= attr.end || (xml[p] != '\'' && xml[p] != '"')) continue;
        const char quote = xml[p++];
        const size_t value_start = p;
        while (p < attr.end && xml[p] != quote) ++p;
        if (p >= attr.end) continue;
        const std::string_view value = xml.substr(value_start, p - value_start);
        if (equals_icase(value, "video/mp4") || equals_icase(value, "video/quicktime") ||
            equals_icase(value, "MotionPhoto")) return true;
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
            equals_icase(local_name(tag_name), "Item") && item_is_motion(xml, p, tag_end))
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

    std::vector<std::pair<size_t, size_t>> motion_ranges;
    if (!find_motion_ranges(xml, motion_ranges)) return false;

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
                if (!is_protocol_attribute(attr.name, protocol)) continue;
                cleaned.append(xml.substr(cursor, attr.start - cursor));
                cursor = attr.end;
                removed_attribute = true;
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
        if (!find_motion_ranges(cleaned, final_ranges)) return false;
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
