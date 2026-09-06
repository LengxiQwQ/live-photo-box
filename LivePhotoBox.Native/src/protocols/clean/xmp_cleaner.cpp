#include "xmp_cleaner.h"
#include "media/media_cleaner.h"
#include "foundation/residue_fingerprint.h"

#include <algorithm>
#include <cctype>
#include <charconv>
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

struct namespace_binding
{
    std::string_view first{};
    std::string_view second{};
};

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
    bindings.push_back({ prefix, uri });
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
            size_t close_name_start = p + 2;
            size_t close_name_end = close_name_start;
            while (close_name_end < tag_end && is_name_char(xml[close_name_end])) ++close_name_end;
            if (close_name_end == close_name_start ||
                xml.substr(close_name_start, close_name_end - close_name_start) != element.name) {
                return false;
            }
            while (close_name_end < tag_end - 1 && std::isspace(static_cast<unsigned char>(xml[close_name_end]))) ++close_name_end;
            if (close_name_end != tag_end - 1) return false;
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

struct canonical_xmp_attribute_identity
{
    std::string_view residue_id{};
    std::string_view selector{};
};

static canonical_xmp_attribute_identity get_canonical_xmp_attribute_identity(
    std::string_view name,
    std::string_view uri,
    lpb_source_protocol protocol) noexcept
{
    const std::string_view local = local_name(name);

    if (protocol == LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1 && uri == google_camera_namespace)
    {
        if (equals_icase(local, "MicroVideo")) return { "google-v1-xmp-microvideo", "GCamera:MicroVideo" };
        if (equals_icase(local, "MicroVideoVersion")) return { "google-v1-xmp-version", "GCamera:MicroVideoVersion" };
        if (equals_icase(local, "MicroVideoOffset")) return { "google-v1-xmp-offset", "GCamera:MicroVideoOffset" };
        if (equals_icase(local, "MicroVideoPresentationTimestampUs")) return { "google-v1-xmp-pts", "GCamera:MicroVideoPresentationTimestampUs" };
        return {};
    }

    if (protocol == LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2 && uri == google_camera_namespace)
    {
        if (equals_icase(local, "MotionPhoto")) return { "google-v2-xmp-motionphoto", "GCamera:MotionPhoto" };
        if (equals_icase(local, "MotionPhotoVersion")) return { "google-v2-xmp-version", "GCamera:MotionPhotoVersion" };
        if (equals_icase(local, "MotionPhotoPresentationTimestampUs")) return { "google-v2-xmp-pts", "GCamera:MotionPhotoPresentationTimestampUs" };
        return {};
    }

    if (protocol == LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG && uri == google_camera_namespace)
    {
        if (equals_icase(local, "MotionPhoto")) return { "samsung-jpeg-xmp-motionphoto", "GCamera:MotionPhoto" };
        if (equals_icase(local, "MotionPhotoVersion")) return { "samsung-jpeg-xmp-version", "GCamera:MotionPhotoVersion" };
        if (equals_icase(local, "MotionPhotoPresentationTimestampUs")) return { "samsung-jpeg-xmp-pts", "GCamera:MotionPhotoPresentationTimestampUs" };
        return {};
    }

    if (protocol == LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC && uri == google_camera_namespace)
    {
        if (equals_icase(local, "MotionPhoto")) return { "samsung-heic-xmp-motionphoto", "GCamera:MotionPhoto" };
        if (equals_icase(local, "MotionPhotoVersion")) return { "samsung-heic-xmp-version", "GCamera:MotionPhotoVersion" };
        if (equals_icase(local, "MotionPhotoPresentationTimestampUs")) return { "samsung-heic-xmp-pts", "GCamera:MotionPhotoPresentationTimestampUs" };
        return {};
    }

    if (protocol == LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO)
    {
        if (uri == oppo_camera_namespace)
        {
            if (equals_icase(local, "OLivePhotoVersion")) return { "oppo-xmp-version", "OLivePhotoVersion" };
            if (equals_icase(local, "VideoLength")) return { "oppo-xmp-videolength", "VideoLength" };
            if (equals_icase(local, "MotionPhotoOwner")) return { "oppo-xmp-owner", "MotionPhotoOwner" };
            if (equals_icase(local, "MotionPhotoPrimaryPresentationTimestampUs")) return { "oppo-xmp-pts", "MotionPhotoPrimaryPresentationTimestampUs" };
            if (equals_icase(local, "MotionPhotoEnable")) return { "oppo-xmp-enable", "MotionPhotoEnable" };
        }
        if (uri == google_camera_namespace)
        {
            if (equals_icase(local, "MotionPhoto")) return { "google-v2-xmp-motionphoto", "GCamera:MotionPhoto" };
            if (equals_icase(local, "MotionPhotoVersion")) return { "google-v2-xmp-version", "GCamera:MotionPhotoVersion" };
            if (equals_icase(local, "MotionPhotoPresentationTimestampUs")) return { "google-v2-xmp-pts", "GCamera:MotionPhotoPresentationTimestampUs" };
        }
        return {};
    }

    if (protocol == LPB_SOURCE_PROTOCOL_VIVO_X300 || protocol == LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL)
    {
        if (uri == vivo_camera_namespace)
        {
            if (equals_icase(local, "VMotionPhotoVersion")) return { "vivo-xmp-version", "VMotionPhotoVersion" };
            if (equals_icase(local, "VMotionPhotoSource")) return { "vivo-xmp-source", "VMotionPhotoSource" };
            if (equals_icase(local, "VMotionPhotoFlags")) return { "vivo-xmp-flags", "VMotionPhotoFlags" };
            if (equals_icase(local, "VMediaKitVersion")) return { "vivo-xmp-mediakit", "VMediaKitVersion" };
        }
        if (uri == google_camera_namespace)
        {
            if (equals_icase(local, "MotionPhoto")) return { "google-v2-xmp-motionphoto", "GCamera:MotionPhoto" };
            if (equals_icase(local, "MotionPhotoVersion")) return { "google-v2-xmp-version", "GCamera:MotionPhotoVersion" };
            if (equals_icase(local, "MotionPhotoPresentationTimestampUs")) return { "google-v2-xmp-pts", "GCamera:MotionPhotoPresentationTimestampUs" };
        }
        return {};
    }

    return {};
}

struct canonical_xmp_container_item_identity
{
    std::string_view residue_id{};
    std::string_view selector{};
};

static canonical_xmp_container_item_identity get_canonical_motion_item_identity(
    lpb_source_protocol protocol) noexcept
{
    switch (protocol)
    {
    case LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2:
        return { "google-v2-container-item-motionphoto", "Item:Semantic=MotionPhoto" };
    case LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG:
        return { "samsung-jpeg-container-item-motionphoto", "Item:Semantic=MotionPhoto" };
    case LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC:
        return { "samsung-heic-container-item-motionphoto", "Item:Semantic=MotionPhoto" };
    case LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO:
        return { "oppo-container-item-motionphoto", "Item:Semantic=MotionPhoto" };
    case LPB_SOURCE_PROTOCOL_VIVO_X300:
    case LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL:
        return { "google-v2-container-item-motionphoto", "Item:Semantic=MotionPhoto" };
    default:
        return {};
    }
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
    return !get_canonical_xmp_attribute_identity(name, uri, protocol).residue_id.empty();
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

static const char* protocol_name_str(lpb_source_protocol p) noexcept {
    switch (p) {
    case LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO: return "Apple";
    case LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1: return "GoogleMicroVideo";
    case LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2: return "GoogleMotionPhoto";
    case LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO: return "OPPO";
    case LPB_SOURCE_PROTOCOL_VIVO_X300: return "vivo";
    case LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL: return "vivo";
    case LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG: return "Samsung";
    case LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC: return "Samsung";
    case LPB_SOURCE_PROTOCOL_HUAWEI_MOVING_PHOTO: return "Huawei";
    case LPB_SOURCE_PROTOCOL_HONOR_MOVING_PHOTO: return "Honor";
    default: return "Source protocol";
    }
}

static const lpb_cleanup_action* find_xmp_attribute_action(
    std::string_view name,
    std::string_view uri,
    lpb_source_protocol protocol,
    const lpb_cleanup_action* actions,
    size_t action_count) noexcept
{
    const auto canon = get_canonical_xmp_attribute_identity(name, uri, protocol);
    if (canon.residue_id.empty()) return nullptr;
    if (!actions || action_count == 0) return nullptr;

    return lpb::media::find_authorized_action(
        actions,
        action_count,
        canon.residue_id,
        LPB_ARTIFACT_PRIMARY_IMAGE,
        LPB_RESIDUE_XMP_PROPERTY,
        canon.selector,
        LPB_REMOVAL_DELETE);
}

static const lpb_cleanup_action* find_motion_item_action(
    const parsed_element& element,
    lpb_source_protocol protocol,
    const lpb_cleanup_action* actions,
    size_t action_count) noexcept
{
    if (!item_is_motion(element, protocol)) return nullptr;
    const auto canon = get_canonical_motion_item_identity(protocol);
    if (canon.residue_id.empty()) return nullptr;
    if (!actions || action_count == 0) return nullptr;

    return lpb::media::find_authorized_action(
        actions,
        action_count,
        canon.residue_id,
        LPB_ARTIFACT_PRIMARY_IMAGE,
        LPB_RESIDUE_XMP_CONTAINER_ITEM,
        canon.selector,
        LPB_REMOVAL_DELETE);
}

static bool find_motion_ranges(std::string_view xml,
    lpb_source_protocol protocol,
    const lpb_cleanup_action* actions,
    size_t action_count,
    std::vector<std::pair<size_t, size_t>>& ranges,
    std::vector<lpb_cleanup_action>& matched_actions,
    std::vector<std::string>& matched_fps) noexcept
{
    std::vector<parsed_element> elements;
    if (!parse_xml_elements(xml, elements)) return false;

    for (const auto& element : elements)
    {
        if (!item_is_motion(element, protocol)) continue;
        if (!actions || action_count == 0) return false;
        const lpb_cleanup_action* matched_act = find_motion_item_action(element, protocol, actions, action_count);
        if (!matched_act) continue;

        std::string_view sem, mime;
        uint64_t len = 0, pad = 0;
        bool has_pad = false;
        for (const auto& a : element.attributes) {
            if (namespace_uri_for_name(a.name, element.bindings, true) != google_item_namespace) continue;
            std::string_view local = local_name(a.name);
            if (equals_icase(local, "Semantic")) sem = a.value;
            else if (equals_icase(local, "Mime")) mime = a.value;
            else if (equals_icase(local, "Length")) {
                std::from_chars(a.value.data(), a.value.data() + a.value.size(), len);
            }
            else if (equals_icase(local, "Padding")) {
                has_pad = true;
                std::from_chars(a.value.data(), a.value.data() + a.value.size(), pad);
            }
        }
        std::string item_fp = lpb::crypto::compute_xmp_container_item_fingerprint(sem, mime, len, pad, has_pad);
        if (matched_act && matched_act->expected_fingerprint[0] != '\0') {
            if (item_fp != matched_act->expected_fingerprint) {
                return false;
            }
        }

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
        if (matched_act) {
            matched_actions.push_back(*matched_act);
        }
        matched_fps.push_back(item_fp);
    }
    return true;
}

} // namespace

bool clean_xmp_metadata_with_plan(
    const std::string& input_xmp,
    lpb_source_protocol protocol,
    const lpb_cleanup_action* actions,
    size_t action_count,
    std::string& output_xmp,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    if (input_xmp.empty() || !actions || action_count == 0) return false;
    const std::string_view xml(input_xmp);

    std::vector<std::pair<size_t, size_t>> motion_ranges;
    std::vector<lpb_cleanup_action> matched_motion_actions;
    std::vector<std::string> matched_motion_fps;
    if (!find_motion_ranges(xml, protocol, actions, action_count, motion_ranges, matched_motion_actions, matched_motion_fps)) return false;

    std::string cleaned;
    cleaned.reserve(xml.size());
    std::vector<parsed_element> elements;
    if (!parse_xml_elements(xml, elements)) return false;
    size_t p = 0;
    bool removed_attribute = false;
    std::vector<lpb_removed_protocol_fact> attr_facts;

    for (const auto& element : elements)
    {
        cleaned.append(xml.substr(p, element.start - p));
        size_t cursor = element.start;
        for (const auto& attr : element.attributes)
        {
            bool should_remove = false;
            const lpb_cleanup_action* matched_act = nullptr;
            if (actions && action_count > 0) {
                matched_act = find_xmp_attribute_action(attr.name,
                    namespace_uri_for_name(attr.name, element.bindings, true), protocol, actions, action_count);
                if (matched_act) should_remove = true;
            } else {
                should_remove = is_protocol_attribute(attr.name,
                    namespace_uri_for_name(attr.name, element.bindings, true), protocol);
            }

            if (should_remove)
            {
                std::string_view attr_uri = namespace_uri_for_name(attr.name, element.bindings, true);
                std::string prop_fp = lpb::crypto::compute_xmp_property_fingerprint(attr_uri, local_name(attr.name), attr.value);
                if (matched_act && matched_act->expected_fingerprint[0] != '\0') {
                    if (prop_fp != matched_act->expected_fingerprint) {
                        return false;
                    }
                }

                cleaned.append(xml.substr(cursor, attr.start - cursor));
                cursor = attr.end;
                removed_attribute = true;

                lpb_removed_protocol_fact fact{};
                fact.struct_size = sizeof(lpb_removed_protocol_fact);
                strncpy_s(fact.protocol_name, protocol_name_str(protocol), _TRUNCATE);
                strncpy_s(fact.component, "XMP attributes", _TRUNCATE);
                std::string desc = std::string("Removed ").append(attr.name);
                strncpy_s(fact.description, desc.c_str(), _TRUNCATE);
                if (matched_act) {
                    strncpy_s(fact.residue_id, matched_act->residue_id, _TRUNCATE);
                    fact.artifact_role = matched_act->artifact_role;
                    fact.structure_kind = matched_act->structure_kind;
                } else {
                    fact.artifact_role = LPB_ARTIFACT_PRIMARY_IMAGE;
                    fact.structure_kind = LPB_RESIDUE_XMP_PROPERTY;
                }
                strncpy_s(fact.operation, "Removed", _TRUNCATE);
                strncpy_s(fact.after_status, "Removed", _TRUNCATE);
                strncpy_s(fact.before_fingerprint, prop_fp.c_str(), _TRUNCATE);
                attr_facts.push_back(fact);
            }
        }
        cleaned.append(xml.substr(cursor, element.start_tag_end - cursor));
        p = element.start_tag_end;
    }
    cleaned.append(xml.substr(p));

    if (!motion_ranges.empty())
    {
        std::vector<std::pair<size_t, size_t>> final_ranges;
        std::vector<lpb_cleanup_action> final_motion_acts;
        std::vector<std::string> final_motion_fps;
        if (!find_motion_ranges(cleaned, protocol, actions, action_count, final_ranges, final_motion_acts, final_motion_fps)) return false;
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

    out_facts.insert(out_facts.end(), attr_facts.begin(), attr_facts.end());
    if (!motion_ranges.empty()) {
        if (!matched_motion_actions.empty()) {
            for (size_t i = 0; i < matched_motion_actions.size(); ++i) {
                const auto& ma = matched_motion_actions[i];
                lpb_removed_protocol_fact fact{};
                fact.struct_size = sizeof(lpb_removed_protocol_fact);
                strncpy_s(fact.protocol_name, protocol_name_str(protocol), _TRUNCATE);
                strncpy_s(fact.component, "XMP Container Directory", _TRUNCATE);
                strncpy_s(fact.description, "Removed validated motion-video item", _TRUNCATE);
                strncpy_s(fact.residue_id, ma.residue_id, _TRUNCATE);
                fact.artifact_role = ma.artifact_role;
                fact.structure_kind = ma.structure_kind;
                strncpy_s(fact.operation, "Removed", _TRUNCATE);
                strncpy_s(fact.after_status, "Removed", _TRUNCATE);
                const char* fp = i < matched_motion_fps.size() ? matched_motion_fps[i].c_str() : "";
                strncpy_s(fact.before_fingerprint, fp, _TRUNCATE);
                out_facts.push_back(fact);
            }
        } else {
            lpb_removed_protocol_fact fact{};
            fact.struct_size = sizeof(lpb_removed_protocol_fact);
            strncpy_s(fact.protocol_name, protocol_name_str(protocol), _TRUNCATE);
            strncpy_s(fact.component, "XMP Container Directory", _TRUNCATE);
            strncpy_s(fact.description, "Removed validated motion-video item", _TRUNCATE);
            fact.artifact_role = LPB_ARTIFACT_PRIMARY_IMAGE;
            fact.structure_kind = LPB_RESIDUE_XMP_CONTAINER_ITEM;
            strncpy_s(fact.operation, "Removed", _TRUNCATE);
            strncpy_s(fact.after_status, "Removed", _TRUNCATE);
            out_facts.push_back(fact);
        }
    }
    return true;
}

bool clean_xmp_metadata(
    const std::string& input_xmp,
    lpb_source_protocol protocol,
    std::string& output_xmp,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    return clean_xmp_metadata_with_plan(input_xmp, protocol, nullptr, 0, output_xmp, out_facts);
}

} // namespace lpb::protocols::clean
