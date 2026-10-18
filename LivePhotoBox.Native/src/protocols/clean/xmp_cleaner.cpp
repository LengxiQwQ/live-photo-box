#include "xmp_cleaner.h"
#include "media/media_cleaner.h"
#include "foundation/residue_fingerprint.h"

#include <algorithm>
#include <cctype>
#include <charconv>
#include <cstring>
#include <limits>
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
static constexpr std::string_view xiaomi_camera_namespace = "http://ns.xiaomi.com/photos/1.0/camera/";

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
    std::string_view semantic{};
};

static canonical_xmp_attribute_identity get_canonical_xmp_attribute_identity(
    std::string_view name,
    std::string_view uri,
    lpb_source_protocol protocol) noexcept
{
    const std::string_view local = local_name(name);

    if (protocol == LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1 && uri == google_camera_namespace)
    {
        if (equals_icase(local, "MicroVideo")) return { "google-v1-xmp-microvideo", "GCamera:MicroVideo", "MicroVideo" };
        if (equals_icase(local, "MicroVideoVersion")) return { "google-v1-xmp-version", "GCamera:MicroVideoVersion", "MicroVideoVersion" };
        if (equals_icase(local, "MicroVideoOffset")) return { "google-v1-xmp-offset", "GCamera:MicroVideoOffset", "MicroVideoOffset" };
        if (equals_icase(local, "MicroVideoPresentationTimestampUs")) return { "google-v1-xmp-pts", "GCamera:MicroVideoPresentationTimestampUs", "MicroVideoPresentationTimestampUs" };
        return {};
    }

    if (protocol == LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2 && uri == google_camera_namespace)
    {
        if (equals_icase(local, "MotionPhoto")) return { "google-v2-xmp-motionphoto", "GCamera:MotionPhoto", "MotionPhoto" };
        if (equals_icase(local, "MotionPhotoVersion")) return { "google-v2-xmp-version", "GCamera:MotionPhotoVersion", "MotionPhotoVersion" };
        if (equals_icase(local, "MotionPhotoPresentationTimestampUs")) return { "google-v2-xmp-pts", "GCamera:MotionPhotoPresentationTimestampUs", "MotionPhotoPresentationTimestampUs" };
        return {};
    }

    if (protocol == LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG && uri == google_camera_namespace)
    {
        if (equals_icase(local, "MotionPhoto")) return { "samsung-jpeg-xmp-motionphoto", "GCamera:MotionPhoto", "MotionPhoto" };
        if (equals_icase(local, "MotionPhotoVersion")) return { "samsung-jpeg-xmp-version", "GCamera:MotionPhotoVersion", "MotionPhotoVersion" };
        if (equals_icase(local, "MotionPhotoPresentationTimestampUs")) return { "samsung-jpeg-xmp-pts", "GCamera:MotionPhotoPresentationTimestampUs", "MotionPhotoPresentationTimestampUs" };
        return {};
    }

    if (protocol == LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC && uri == google_camera_namespace)
    {
        if (equals_icase(local, "MotionPhoto")) return { "samsung-heic-xmp-motionphoto", "GCamera:MotionPhoto", "MotionPhoto" };
        if (equals_icase(local, "MotionPhotoVersion")) return { "samsung-heic-xmp-version", "GCamera:MotionPhotoVersion", "MotionPhotoVersion" };
        if (equals_icase(local, "MotionPhotoPresentationTimestampUs")) return { "samsung-heic-xmp-pts", "GCamera:MotionPhotoPresentationTimestampUs", "MotionPhotoPresentationTimestampUs" };
        return {};
    }

    if (protocol == LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO)
    {
        if (uri == oppo_camera_namespace)
        {
            if (equals_icase(local, "OLivePhotoVersion")) return { "oppo-xmp-version", "OLivePhotoVersion", "OLivePhotoVersion" };
            if (equals_icase(local, "VideoLength")) return { "oppo-xmp-videolength", "VideoLength", "VideoLength" };
            if (equals_icase(local, "MotionPhotoOwner")) return { "oppo-xmp-owner", "MotionPhotoOwner", "MotionPhotoOwner" };
            if (equals_icase(local, "MotionPhotoPrimaryPresentationTimestampUs")) return { "oppo-xmp-pts", "MotionPhotoPrimaryPresentationTimestampUs", "MotionPhotoPrimaryPresentationTimestampUs" };
            if (equals_icase(local, "MotionPhotoEnable")) return { "oppo-xmp-enable", "MotionPhotoEnable", "MotionPhotoEnable" };
        }
        if (uri == google_camera_namespace)
        {
            if (equals_icase(local, "MotionPhoto")) return { "google-v2-xmp-motionphoto", "GCamera:MotionPhoto", "MotionPhoto" };
            if (equals_icase(local, "MotionPhotoVersion")) return { "google-v2-xmp-version", "GCamera:MotionPhotoVersion", "MotionPhotoVersion" };
            if (equals_icase(local, "MotionPhotoPresentationTimestampUs")) return { "google-v2-xmp-pts", "GCamera:MotionPhotoPresentationTimestampUs", "MotionPhotoPresentationTimestampUs" };
        }
        return {};
    }

    if (protocol == LPB_SOURCE_PROTOCOL_VIVO_X300 || protocol == LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL)
    {
        if (uri == vivo_camera_namespace)
        {
            if (equals_icase(local, "VMotionPhotoVersion")) return { "vivo-xmp-version", "VMotionPhotoVersion", "VMotionPhotoVersion" };
            if (equals_icase(local, "VMotionPhotoSource")) return { "vivo-xmp-source", "VMotionPhotoSource", "VMotionPhotoSource" };
            if (equals_icase(local, "VMotionPhotoFlags")) return { "vivo-xmp-flags", "VMotionPhotoFlags", "VMotionPhotoFlags" };
            if (equals_icase(local, "VMediaKitVersion")) return { "vivo-xmp-mediakit", "VMediaKitVersion", "VMediaKitVersion" };
        }
        if (uri == google_camera_namespace)
        {
            if (equals_icase(local, "MotionPhoto")) return { "google-v2-xmp-motionphoto", "GCamera:MotionPhoto", "MotionPhoto" };
            if (equals_icase(local, "MotionPhotoVersion")) return { "google-v2-xmp-version", "GCamera:MotionPhotoVersion", "MotionPhotoVersion" };
            if (equals_icase(local, "MotionPhotoPresentationTimestampUs")) return { "google-v2-xmp-pts", "GCamera:MotionPhotoPresentationTimestampUs", "MotionPhotoPresentationTimestampUs" };
        }
        return {};
    }

    return {};
}

struct canonical_xmp_container_item_identity
{
    std::string_view residue_id{};
    std::string_view selector{};
    std::string_view semantic{};
};

static canonical_xmp_container_item_identity get_canonical_motion_item_identity(
    lpb_source_protocol protocol) noexcept
{
    switch (protocol)
    {
    case LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2:
        return { "google-v2-container-item-motionphoto", "Item:Semantic=MotionPhoto", "MotionPhoto" };
    case LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG:
        return { "samsung-jpeg-container-item-motionphoto", "Item:Semantic=MotionPhoto", "MotionPhoto" };
    case LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC:
        return { "samsung-heic-container-item-motionphoto", "Item:Semantic=MotionPhoto", "MotionPhoto" };
    case LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO:
        return { "oppo-container-item-motionphoto", "Item:Semantic=MotionPhoto", "MotionPhoto" };
    case LPB_SOURCE_PROTOCOL_VIVO_X300:
    case LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL:
        return { "google-v2-container-item-motionphoto", "Item:Semantic=MotionPhoto", "MotionPhoto" };
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
        protocol,
        canon.residue_id,
        LPB_ARTIFACT_PRIMARY_IMAGE,
        LPB_RESIDUE_XMP_PROPERTY,
        canon.selector,
        canon.semantic,
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
        protocol,
        canon.residue_id,
        LPB_ARTIFACT_PRIMARY_IMAGE,
        LPB_RESIDUE_XMP_CONTAINER_ITEM,
        canon.selector,
        canon.semantic,
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
        if (!matched_act || matched_act->expected_fingerprint[0] == '\0' || item_fp != matched_act->expected_fingerprint) {
            return false;
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

struct micamera_nested_item_offset
{
    uint64_t offset{};
    uint64_t length{};
    size_t offset_value_start{};
    size_t offset_value_end{};
};

static std::string_view fixed_utf8_string_view(const char* value, size_t capacity) noexcept
{
    if (!value || capacity == 0) return {};
    const auto* const terminator = static_cast<const char*>(std::memchr(value, '\0', capacity));
    if (!terminator) return {};
    const auto distance = terminator - value;
    if (distance < 0) return {};
    const size_t checked_length = static_cast<size_t>(distance);
    if (checked_length >= capacity) return {};
    const auto length = static_cast<std::string_view::size_type>(checked_length);
    return std::string_view(value, length);
}

static bool parse_unsigned_decimal(std::string_view value, uint64_t& out_value) noexcept
{
    if (value.empty()) return false;
    uint64_t parsed = 0;
    const auto result = std::from_chars(value.data(), value.data() + value.size(), parsed, 10);
    if (result.ec != std::errc{} || result.ptr != value.data() + value.size()) return false;
    out_value = parsed;
    return true;
}

static bool checked_media_range_end(const lpb_media_range& range, uint64_t& out_end) noexcept
{
    if (range.length > std::numeric_limits<uint64_t>::max() - range.offset) return false;
    out_end = range.offset + range.length;
    return true;
}

static bool is_micamera_fact_candidate(
    const lpb_auxiliary_item_facts& item,
    std::string_view stable_identity,
    std::string_view semantic,
    std::string_view relationship) noexcept
{
    return fixed_utf8_string_view(item.stable_identity, sizeof(item.stable_identity)) == stable_identity ||
        fixed_utf8_string_view(item.semantic, sizeof(item.semantic)) == semantic ||
        fixed_utf8_string_view(item.relationship, sizeof(item.relationship)) == relationship;
}

static bool micamera_fact_matches(
    const lpb_auxiliary_item_facts& item,
    std::string_view stable_identity,
    std::string_view semantic,
    std::string_view relationship,
    lpb_auxiliary_codec codec) noexcept
{
    return item.struct_size >= sizeof(lpb_auxiliary_item_facts) &&
        item.is_present == 1 &&
        fixed_utf8_string_view(item.stable_identity, sizeof(item.stable_identity)) == stable_identity &&
        fixed_utf8_string_view(item.semantic, sizeof(item.semantic)) == semantic &&
        fixed_utf8_string_view(item.relationship, sizeof(item.relationship)) == relationship &&
        fixed_utf8_string_view(item.owner_identity, sizeof(item.owner_identity)) == "primary:0" &&
        item.source_index == 0 &&
        item.container == LPB_IMAGE_CONTAINER_JPEG &&
        item.representation == LPB_AUX_REPRESENTATION_EMBEDDED &&
        item.ownership == LPB_AUX_OWNER_PRIMARY &&
        item.codec == codec;
}

static bool parse_micamera_nested_target_tag(
    std::string_view raw_tag,
    size_t raw_tag_start,
    std::string_view expected_name,
    bool& out_is_target,
    micamera_nested_item_offset& out_item) noexcept
{
    out_is_target = false;
    size_t position = 0;
    bool closing_tag = false;
    if (position < raw_tag.size() && raw_tag[position] == '/') {
        closing_tag = true;
        ++position;
    }

    const size_t name_start = position;
    while (position < raw_tag.size() && is_name_char(raw_tag[position])) ++position;
    if (position == name_start) return closing_tag || raw_tag.empty();

    const std::string_view name = raw_tag.substr(name_start, position - name_start);
    if (!equals_icase(name, expected_name)) return true;
    out_is_target = true;
    if (name != expected_name || closing_tag) return false;

    bool saw_offset = false;
    bool saw_length = false;
    bool self_closing = false;
    micamera_nested_item_offset parsed{};
    constexpr std::string_view encoded_quote = "&quot;";

    while (position < raw_tag.size()) {
        while (position < raw_tag.size() && std::isspace(static_cast<unsigned char>(raw_tag[position]))) ++position;
        if (position == raw_tag.size()) break;
        if (raw_tag[position] == '/') {
            ++position;
            while (position < raw_tag.size() && std::isspace(static_cast<unsigned char>(raw_tag[position]))) ++position;
            if (position != raw_tag.size()) return false;
            self_closing = true;
            break;
        }

        const size_t attribute_name_start = position;
        while (position < raw_tag.size() && is_name_char(raw_tag[position])) ++position;
        if (position == attribute_name_start) return false;
        const std::string_view attribute_name = raw_tag.substr(attribute_name_start, position - attribute_name_start);
        while (position < raw_tag.size() && std::isspace(static_cast<unsigned char>(raw_tag[position]))) ++position;
        if (position >= raw_tag.size() || raw_tag[position] != '=') return false;
        ++position;
        while (position < raw_tag.size() && std::isspace(static_cast<unsigned char>(raw_tag[position]))) ++position;
        if (raw_tag.substr(position, encoded_quote.size()) != encoded_quote) return false;
        position += encoded_quote.size();
        const size_t value_start = position;
        const size_t value_end = raw_tag.find(encoded_quote, value_start);
        if (value_end == std::string_view::npos) return false;
        const std::string_view value = raw_tag.substr(value_start, value_end - value_start);

        if (attribute_name == "offset") {
            if (saw_offset || !parse_unsigned_decimal(value, parsed.offset)) return false;
            saw_offset = true;
            parsed.offset_value_start = raw_tag_start + value_start;
            parsed.offset_value_end = raw_tag_start + value_end;
        } else if (attribute_name == "length") {
            if (saw_length || !parse_unsigned_decimal(value, parsed.length)) return false;
            saw_length = true;
        }
        position = value_end + encoded_quote.size();
    }

    if (!self_closing || !saw_offset || !saw_length) return false;
    out_item = parsed;
    return true;
}

static bool parse_micamera_xmpmeta_offsets(
    std::string_view encoded_xmpmeta,
    micamera_nested_item_offset& out_subimage,
    micamera_nested_item_offset& out_lenswatermark,
    size_t& out_subimage_count,
    size_t& out_lenswatermark_count) noexcept
{
    size_t cursor = 0;
    out_subimage_count = 0;
    out_lenswatermark_count = 0;
    bool saw_xml_declaration = false;
    constexpr std::string_view encoded_open = "&lt;";
    constexpr std::string_view encoded_close = "&gt;";

    while ((cursor = encoded_xmpmeta.find(encoded_open, cursor)) != std::string_view::npos) {
        const size_t tag_start = cursor + encoded_open.size();
        const size_t tag_end = encoded_xmpmeta.find(encoded_close, tag_start);
        if (tag_end == std::string_view::npos) return false;
        const std::string_view raw_tag = encoded_xmpmeta.substr(tag_start, tag_end - tag_start);
        if (!raw_tag.empty() && raw_tag.front() == '?') {
            if (saw_xml_declaration || raw_tag.size() < 5 || raw_tag.substr(0, 4) != "?xml" || raw_tag.back() != '?') return false;
            saw_xml_declaration = true;
            cursor = tag_end + encoded_close.size();
            continue;
        }

        bool is_target = false;
        micamera_nested_item_offset parsed{};
        if (!parse_micamera_nested_target_tag(raw_tag, tag_start, "subimage", is_target, parsed)) return false;
        if (is_target) {
            if (++out_subimage_count != 1) return false;
            out_subimage = parsed;
        }
        if (!parse_micamera_nested_target_tag(raw_tag, tag_start, "lenswatermark", is_target, parsed)) return false;
        if (is_target) {
            if (++out_lenswatermark_count != 1) return false;
            out_lenswatermark = parsed;
        }
        cursor = tag_end + encoded_close.size();
    }

    return true;
}

} // namespace

bool rebase_micamera_xmpmeta_offsets_for_google_v1_clean(
    const std::string& input_xmp,
    const lpb_source_media_facts& source_facts,
    uint64_t extracted_primary_length,
    std::string& output_xmp)
{
    if (input_xmp.empty() || source_facts.struct_size < sizeof(lpb_source_media_facts) ||
        source_facts.protocol != LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1 ||
        source_facts.primary_image.is_present != 1 ||
        source_facts.primary_image.container != LPB_IMAGE_CONTAINER_JPEG ||
        source_facts.primary_image.file_range.offset != 0 ||
        source_facts.primary_image.file_range.length != extracted_primary_length ||
        extracted_primary_length == 0 ||
        source_facts.auxiliary_count > (sizeof(source_facts.auxiliary_items) / sizeof(source_facts.auxiliary_items[0]))) {
        return false;
    }

    const lpb_auxiliary_item_facts* subimage_fact = nullptr;
    const lpb_auxiliary_item_facts* lenswatermark_fact = nullptr;
    for (uint32_t i = 0; i < source_facts.auxiliary_count; ++i) {
        const auto& item = source_facts.auxiliary_items[i];
        const bool is_subimage = is_micamera_fact_candidate(
            item, "micamera:xmpmeta:subimage", "MiCameraSubimage", "micamera-xmpmeta-subimage");
        const bool is_lenswatermark = is_micamera_fact_candidate(
            item, "micamera:xmpmeta:lenswatermark", "MiCameraLensWatermark", "micamera-xmpmeta-lenswatermark");
        if (is_subimage && is_lenswatermark) return false;
        if (is_subimage) {
            if (subimage_fact) return false;
            subimage_fact = &item;
        }
        if (is_lenswatermark) {
            if (lenswatermark_fact) return false;
            lenswatermark_fact = &item;
        }
    }

    std::vector<parsed_element> elements;
    if (!parse_xml_elements(input_xmp, elements)) return false;
    std::string_view xmpmeta_value;
    size_t xmpmeta_count = 0;
    for (const auto& element : elements) {
        for (const auto& attribute : element.attributes) {
            if (namespace_uri_for_name(attribute.name, element.bindings, true) != xiaomi_camera_namespace ||
                !equals_icase(local_name(attribute.name), "XMPMeta")) continue;
            if (++xmpmeta_count != 1) return false;
            xmpmeta_value = attribute.value;
        }
    }

    micamera_nested_item_offset subimage_xmp{};
    micamera_nested_item_offset lenswatermark_xmp{};
    size_t nested_subimage_count = 0;
    size_t nested_lenswatermark_count = 0;
    if (xmpmeta_count == 1 && !parse_micamera_xmpmeta_offsets(
            xmpmeta_value, subimage_xmp, lenswatermark_xmp, nested_subimage_count, nested_lenswatermark_count)) {
        return false;
    }

    if (!subimage_fact && !lenswatermark_fact) {
        if (nested_subimage_count != 0 || nested_lenswatermark_count != 0) return false;
        output_xmp = input_xmp;
        return true;
    }
    if (!subimage_fact || !lenswatermark_fact || xmpmeta_count != 1) return false;
    if (nested_subimage_count != 1 || nested_lenswatermark_count != 1) return false;

    if (!micamera_fact_matches(*subimage_fact,
            "micamera:xmpmeta:subimage", "MiCameraSubimage", "micamera-xmpmeta-subimage", LPB_AUX_CODEC_JPEG) ||
        !micamera_fact_matches(*lenswatermark_fact,
            "micamera:xmpmeta:lenswatermark", "MiCameraLensWatermark", "micamera-xmpmeta-lenswatermark", LPB_AUX_CODEC_COPY)) {
        return false;
    }

    const auto& primary_range = source_facts.primary_image.file_range;
    const auto& video = source_facts.motion_video;
    uint64_t primary_end = 0;
    uint64_t source_end = 0;
    uint64_t subimage_end = 0;
    uint64_t lenswatermark_end = 0;
    if (!checked_media_range_end(primary_range, primary_end) ||
        video.is_present != 1 || video.container != LPB_VIDEO_CONTAINER_MP4 || video.source_index != 0 ||
        video.file_range.offset != primary_end || video.file_range.length == 0 ||
        !checked_media_range_end(video.file_range, source_end) || source_end <= primary_end ||
        source_facts.protocol_tail_range.length != 0 ||
        subimage_fact->file_range.length == 0 || lenswatermark_fact->file_range.length == 0 ||
        subimage_fact->file_range.offset < primary_range.offset ||
        lenswatermark_fact->file_range.offset < primary_range.offset ||
        !checked_media_range_end(subimage_fact->file_range, subimage_end) ||
        !checked_media_range_end(lenswatermark_fact->file_range, lenswatermark_end) ||
        subimage_end != lenswatermark_fact->file_range.offset ||
        lenswatermark_end != primary_end ||
        subimage_fact->file_range.offset >= lenswatermark_fact->file_range.offset) {
        return false;
    }

    const uint64_t expected_subimage_old_offset = source_end - subimage_fact->file_range.offset;
    const uint64_t expected_lenswatermark_old_offset = source_end - lenswatermark_fact->file_range.offset;
    const uint64_t expected_subimage_new_offset = primary_end - subimage_fact->file_range.offset;
    const uint64_t expected_lenswatermark_new_offset = primary_end - lenswatermark_fact->file_range.offset;
    if (subimage_xmp.offset != expected_subimage_old_offset ||
        lenswatermark_xmp.offset != expected_lenswatermark_old_offset ||
        subimage_xmp.length != subimage_fact->file_range.length ||
        lenswatermark_xmp.length != lenswatermark_fact->file_range.length ||
        expected_subimage_new_offset == 0 || expected_lenswatermark_new_offset == 0) {
        return false;
    }

    const size_t value_base = static_cast<size_t>(xmpmeta_value.data() - input_xmp.data());
    struct offset_patch { size_t start; size_t end; std::string replacement; };
    std::vector<offset_patch> patches;
    patches.push_back({ value_base + subimage_xmp.offset_value_start, value_base + subimage_xmp.offset_value_end,
        std::to_string(expected_subimage_new_offset) });
    patches.push_back({ value_base + lenswatermark_xmp.offset_value_start, value_base + lenswatermark_xmp.offset_value_end,
        std::to_string(expected_lenswatermark_new_offset) });
    std::sort(patches.begin(), patches.end(), [](const auto& left, const auto& right) { return left.start > right.start; });

    output_xmp = input_xmp;
    for (const auto& patch : patches) {
        if (patch.start > patch.end || patch.end > output_xmp.size()) return false;
        output_xmp.replace(patch.start, patch.end - patch.start, patch.replacement);
    }
    return true;
}

namespace {

struct neutral_directory_item_span
{
    neutral_container_item item;
    size_t li_start{};
    size_t li_end{};
};

static size_t direct_parent_index(const std::vector<parsed_element>& elements, size_t child_index) noexcept
{
    if (child_index >= elements.size()) return static_cast<size_t>(-1);
    const auto& child = elements[child_index];
    size_t best_parent = static_cast<size_t>(-1);
    size_t best_span = std::numeric_limits<size_t>::max();
    for (size_t index = 0; index < elements.size(); ++index)
    {
        if (index == child_index) continue;
        const auto& candidate = elements[index];
        if (candidate.start >= child.start || candidate.end < child.end) continue;
        const size_t span = candidate.end - candidate.start;
        if (span < best_span)
        {
            best_parent = index;
            best_span = span;
        }
    }
    return best_parent;
}

static bool is_element(const parsed_element& element, std::string_view uri,
    std::string_view local) noexcept
{
    return local_name(element.name) == local &&
        namespace_uri_for_name(element.name, element.bindings, false) == uri;
}

static bool parse_neutral_directory(std::string_view xml,
    std::vector<neutral_directory_item_span>& out_items)
{
    out_items.clear();
    std::vector<parsed_element> elements;
    if (!parse_xml_elements(xml, elements)) return false;
    std::vector<size_t> parents(elements.size(), static_cast<size_t>(-1));
    for (size_t index = 0; index < elements.size(); ++index)
    {
        parents[index] = direct_parent_index(elements, index);
    }

    size_t directory_index = static_cast<size_t>(-1);
    for (size_t index = 0; index < elements.size(); ++index)
    {
        if (!is_element(elements[index], google_container_namespace, "Directory")) continue;
        if (directory_index != static_cast<size_t>(-1)) return false;
        directory_index = index;
    }
    if (directory_index == static_cast<size_t>(-1)) return false;

    size_t sequence_index = static_cast<size_t>(-1);
    for (size_t index = 0; index < elements.size(); ++index)
    {
        if (parents[index] != directory_index) continue;
        if (!is_element(elements[index], rdf_namespace, "Seq") ||
            sequence_index != static_cast<size_t>(-1)) return false;
        sequence_index = index;
    }
    if (sequence_index == static_cast<size_t>(-1)) return false;

    std::vector<neutral_directory_item_span> parsed_items;
    for (size_t li_index = 0; li_index < elements.size(); ++li_index)
    {
        if (parents[li_index] != sequence_index) continue;
        const auto& li = elements[li_index];
        if (!is_element(li, rdf_namespace, "li")) return false;

        size_t item_index = static_cast<size_t>(-1);
        for (size_t child_index = 0; child_index < elements.size(); ++child_index)
        {
            if (parents[child_index] != li_index) continue;
            if (!is_element(elements[child_index], google_container_namespace, "Item") ||
                item_index != static_cast<size_t>(-1)) return false;
            item_index = child_index;
        }
        if (item_index == static_cast<size_t>(-1)) return false;
        for (size_t child_index = 0; child_index < elements.size(); ++child_index)
        {
            if (parents[child_index] == item_index) return false;
        }

        neutral_container_item item{};
        item.ordinal = parsed_items.size();
        bool found_semantic = false;
        bool found_mime = false;
        bool found_length = false;
        for (const auto& attribute : elements[item_index].attributes)
        {
            if (namespace_uri_for_name(attribute.name, elements[item_index].bindings, true) != google_item_namespace) continue;
            const std::string_view local = local_name(attribute.name);
            if (equals_icase(local, "Semantic"))
            {
                if (found_semantic || attribute.value.empty()) return false;
                item.semantic.assign(attribute.value);
                found_semantic = true;
            }
            else if (equals_icase(local, "Mime"))
            {
                if (found_mime || attribute.value.empty()) return false;
                item.mime.assign(attribute.value);
                found_mime = true;
            }
            else if (equals_icase(local, "Length"))
            {
                if (found_length) return false;
                uint64_t length = 0;
                const auto result = std::from_chars(attribute.value.data(),
                    attribute.value.data() + attribute.value.size(), length);
                if (result.ec != std::errc{} || result.ptr != attribute.value.data() + attribute.value.size()) return false;
                item.length = length;
                item.has_length = true;
                found_length = true;
            }
        }
        if (!found_semantic || !found_mime) return false;
        parsed_items.push_back({ std::move(item), li.start, li.end });
    }

    if (parsed_items.size() < 2 || parsed_items.front().item.semantic != "Primary" ||
        parsed_items.front().item.mime != "image/jpeg") return false;
    size_t gainmap_count = 0;
    std::vector<std::string> seen_semantics;
    seen_semantics.reserve(parsed_items.size());
    for (const auto& record : parsed_items)
    {
        const auto& item = record.item;
        if (std::find(seen_semantics.begin(), seen_semantics.end(), item.semantic) != seen_semantics.end()) return false;
        seen_semantics.push_back(item.semantic);
        if (item.semantic == "MotionPhoto") return false;
        if (item.semantic == "GainMap")
        {
            ++gainmap_count;
            if (item.mime != "image/jpeg" || !item.has_length || item.length == 0) return false;
        }
        else if (item.semantic != "Primary" &&
            (item.mime != "image/jpeg" || !item.has_length || item.length == 0))
        {
            // Only explicitly sized JPEG directory items can be mapped to a
            // bounded source MPF entry and safely detached from this owner.
            return false;
        }
    }
    if (gainmap_count != 1) return false;
    out_items = std::move(parsed_items);
    return true;
}

} // namespace

bool canonicalize_neutral_gainmap_directory(
    const std::string& input_xmp,
    uint64_t expected_gainmap_length,
    std::string& output_xmp,
    neutral_gainmap_directory& out_directory)
{
    output_xmp.clear();
    out_directory = {};
    if (input_xmp.empty() || expected_gainmap_length == 0) return false;

    std::vector<neutral_directory_item_span> source_items;
    if (!parse_neutral_directory(input_xmp, source_items)) return false;
    const auto gainmap = std::find_if(source_items.begin(), source_items.end(),
        [](const neutral_directory_item_span& item) { return item.item.semantic == "GainMap"; });
    if (gainmap == source_items.end() || gainmap->item.length != expected_gainmap_length) return false;

    out_directory.source_item_count = source_items.size();
    out_directory.gainmap_ordinal = gainmap->item.ordinal;
    out_directory.source_items.reserve(source_items.size());
    for (const auto& record : source_items) out_directory.source_items.push_back(record.item);

    output_xmp = input_xmp;
    bool removed_extra = false;
    for (const auto& record : source_items)
    {
        if (record.item.semantic == "Primary" || record.item.semantic == "GainMap") continue;
        if (record.li_start > record.li_end || record.li_end > output_xmp.size()) return false;
        for (size_t index = record.li_start; index < record.li_end; ++index)
        {
            if (output_xmp[index] != '\r' && output_xmp[index] != '\n') output_xmp[index] = ' ';
        }
        removed_extra = true;
    }

    if (output_xmp.size() != input_xmp.size()) return false;
    if (removed_extra)
    {
        std::vector<neutral_directory_item_span> final_items;
        if (!parse_neutral_directory(output_xmp, final_items) || final_items.size() != 2 ||
            final_items[0].item.semantic != "Primary" || final_items[1].item.semantic != "GainMap" ||
            final_items[1].item.length != expected_gainmap_length) return false;
    }
    return true;
}

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
                if (!matched_act || matched_act->expected_fingerprint[0] == '\0' || prop_fp != matched_act->expected_fingerprint) {
                    return false;
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
