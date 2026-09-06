#include "containers/mp4_strip.h"
#include "foundation/residue_fingerprint.h"
#include "foundation/internal.h"
#include "containers/isobmff.h"
#include <fstream>
#include <filesystem>
#include <algorithm>
#include <limits>
#include <vector>
#include <cstring>
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>

static bool is_printable_type(const uint8_t* p) {
    for (int i = 0; i < 4; i++) {
        if (p[i] < 0x20 || p[i] > 0x7E) return false;
    }
    return true;
}

static size_t get_meta_children_start(std::span<const uint8_t> data, size_t meta_start, size_t meta_end) {
    if (meta_start + 16 <= meta_end) {
        int32_t probe_size = read_be32(data.data() + meta_start + 8);
        if (probe_size >= 8 && meta_start + 8 + static_cast<size_t>(probe_size) <= meta_end && is_printable_type(data.data() + meta_start + 12)) {
            return meta_start + 8;
        }
    }
    return meta_start + 12;
}

static std::string read_key_name(const uint8_t* data, size_t entry_start, size_t entry_size) {
    size_t name_len = entry_size - 8;
    if (name_len >= 6 && entry_size >= 16) {
        int32_t apple_len_signed = (static_cast<int32_t>(data[entry_start + 12]) << 8) | data[entry_start + 13];
        if (apple_len_signed > 0 && entry_start + 14 + static_cast<size_t>(apple_len_signed) == entry_start + entry_size) {
            return std::string(reinterpret_cast<const char*>(data + entry_start + 14), static_cast<size_t>(apple_len_signed));
        }
    }
    if (name_len > 0) {
        return std::string(reinterpret_cast<const char*>(data + entry_start + 8), name_len);
    }
    return "";
}

static std::string read_ilst_value(const uint8_t* data, size_t start, size_t end) {
    size_t cp = start;
    while (cp + 8 <= end) {
        int32_t child_size = read_be32(data + cp);
        if (child_size < 8 || cp + static_cast<size_t>(child_size) > end) break;
        if (data[cp + 4] == 'd' && data[cp + 5] == 'a' && data[cp + 6] == 't' && data[cp + 7] == 'a' && child_size >= 16) {
            size_t value_len = static_cast<size_t>(child_size) - 16;
            if (value_len > 0) {
                return std::string(reinterpret_cast<const char*>(data + cp + 16), value_len);
            }
        }
        cp += static_cast<size_t>(child_size);
    }
    return "";
}

static bool contains_icase(const std::string& str, const char* substr) {
    if (!substr) return false;
    size_t len = std::strlen(substr);
    if (len == 0) return true;
    auto it = std::search(str.begin(), str.end(), substr, substr + len,
        [](char ch1, char ch2) { return std::tolower(static_cast<unsigned char>(ch1)) == std::tolower(static_cast<unsigned char>(ch2)); });
    return it != str.end();
}

static bool starts_with_icase(const std::string& str, const char* prefix) {
    if (!prefix) return false;
    size_t len = std::strlen(prefix);
    if (str.length() < len) return false;
    for (size_t i = 0; i < len; ++i) {
        if (std::tolower(static_cast<unsigned char>(str[i])) != std::tolower(static_cast<unsigned char>(prefix[i]))) return false;
    }
    return true;
}

static bool has_complete_top_level_boxes(const uint8_t* data, size_t data_size) noexcept
{
    size_t position = 0;
    if (!data && data_size != 0) return false;
    while (position < data_size) {
        isobmff_box_header box{};
        if (!try_read_box_header(data, position, data_size, box)) return false;
        position += box.size;
    }
    return position == data_size;
}

struct box_entry { size_t start; size_t size; std::string name; };
struct ilst_item { size_t start; size_t size; int32_t index; std::string value; };

static std::vector<uint8_t> rebuild_container(
    std::span<const uint8_t> data, size_t box_start, size_t box_end, size_t children_start,
    const std::vector<uint8_t>* replace_a, size_t replace_a_pos,
    const std::vector<uint8_t>* replace_b, size_t replace_b_pos)
{
    if (box_start > box_end || box_end > data.size() || children_start < box_start + 8 || children_start > box_end) return {};
    isobmff_box_header container{};
    if (!try_read_box_header(data.data(), box_start, box_end, container) || container.header_size != 8 || box_start + container.size != box_end) return {};
    std::vector<uint8_t> result;
    result.reserve(box_end - box_start + 64);
    result.insert(result.end(), 4, 0);
    result.insert(result.end(), data.data() + box_start + 4, data.data() + box_start + 8);
    
    if (children_start == box_start + 12) {
        result.insert(result.end(), data.data() + box_start + 8, data.data() + box_start + 12);
    }
    
    size_t pos = children_start;
    while (pos < box_end) {
        isobmff_box_header child{};
        if (!try_read_box_header(data.data(), pos, box_end, child)) return {};
        const size_t child_size = child.size;
        
        if (pos == replace_a_pos && replace_a != nullptr) {
            result.insert(result.end(), replace_a->begin(), replace_a->end());
        }
        else if (pos == replace_b_pos && replace_b != nullptr) {
            result.insert(result.end(), replace_b->begin(), replace_b->end());
        }
        else {
            result.insert(result.end(), data.data() + pos, data.data() + pos + child_size);
        }
        pos += child_size;
    }
    if (pos != box_end || result.size() > std::numeric_limits<uint32_t>::max()) return {};
    write_be32(result.data(), static_cast<int32_t>(result.size()));
    return result;
}

extern "C" lpb_result LPB_CALL lpb_mp4_strip_uuid_box(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    const uint8_t* user_type_16,
    uint8_t* output,
    size_t output_size,
    size_t* out_written)
{
    if (context == nullptr || input == nullptr || user_type_16 == nullptr || out_written == nullptr)
        return LPB_RESULT_INVALID_ARGUMENT;

    if (input_size < 8 || !has_complete_top_level_boxes(input, input_size)) {
        set_error(context, "Input video contains a malformed top-level ISO-BMFF box.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    struct target_box { size_t start; size_t size; };
    std::vector<target_box> targets;
    size_t position = 0;
    while (position + 8 <= input_size)
    {
        const uint32_t size32 = read_be32u(input + position);
        uint64_t size = size32;
        size_t header_size = 8;
        if (size32 == 1)
        {
            if (position + 16 > input_size) break;
            const int64_t extended_size = read_be64(input + position + 8);
            if (extended_size < 0) break;
            size = static_cast<uint64_t>(extended_size);
            header_size = 16;
        }
        else if (size32 == 0)
        {
            size = input_size - position;
        }

        if (size < header_size || size > static_cast<uint64_t>(std::numeric_limits<int32_t>::max())
            || size > input_size - position)
        {
            break;
        }
        const size_t box_size = static_cast<size_t>(size);
        const bool uuid = input[position + 4] == 'u' && input[position + 5] == 'u'
            && input[position + 6] == 'i' && input[position + 7] == 'd';
        
        if (uuid && box_size >= header_size + 16)
        {
            if (std::memcmp(input + position + header_size, user_type_16, 16) == 0)
            {
                targets.push_back({ position, box_size });
            }
        }
        position += box_size;
    }

    if (targets.empty())
    {
        *out_written = 0;
        return LPB_RESULT_OK;
    }

    size_t removed = 0;
    for (const target_box& target : targets)
    {
        removed += target.size;
    }
    
    std::vector<uint8_t> result;
    result.reserve(input_size - removed);
    size_t source = 0;
    for (const target_box& target : targets)
    {
        if (target.start > source)
        {
            result.insert(result.end(), input + source, input + target.start);
        }
        source = target.start + target.size;
    }
    if (input_size > source)
    {
        result.insert(result.end(), input + source, input + input_size);
    }

    const size_t moov = find_top_level_box(result, "moov");
    if (moov != std::numeric_limits<size_t>::max())
    {
        size_t prior_removed = 0;
        for (const target_box& target : targets) {
            if (target.start < prior_removed ||
                !adjust_chunk_offsets(result, moov, target.start - prior_removed, target.size)) {
                set_error(context, "Unable to safely relocate MP4 chunk offsets after UUID removal.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            prior_removed += target.size;
        }
    }

    return copy_output(context, result, output, output_size, out_written);
}

static bool rebuild_moov_without_matching_tracks(
    lpb_context* context,
    std::span<const uint8_t> data,
    const char* const* key_fragments,
    size_t fragment_count,
    std::vector<uint8_t>& out_new_moov,
    bool& out_modified,
    std::vector<bool>* out_matched = nullptr,
    std::vector<std::string>* out_fps = nullptr,
    const lpb_cleanup_action* actions = nullptr,
    size_t action_count = 0,
    lpb_source_protocol expected_protocol = LPB_SOURCE_PROTOCOL_UNKNOWN)
{
    out_modified = false;
    const size_t missing = std::numeric_limits<size_t>::max();
    if (data.size() < 8 || !is_type(data, 0, "moov")) return false;
    isobmff_box_header moov_header{};
    if (!try_read_box_header(data.data(), 0, data.size(), moov_header) || moov_header.size != data.size()) {
        set_error(context, "Malformed moov box.");
        return false;
    }
    const size_t moov_size = moov_header.size;
    const size_t moov_end = moov_size;

    std::vector<size_t> remove_pos;
    size_t pos = 8;
    while (pos + 8 <= moov_end)
    {
        isobmff_box_header child{};
        if (!try_read_box_header(data.data(), pos, moov_end, child)) {
            set_error(context, "Malformed moov child box.");
            return false;
        }
        const size_t size = child.size;

        if (is_type(data, pos, "trak"))
        {
            const size_t trak_start = pos;
            const size_t trak_end = pos + size;
            const size_t mdia = find_child_box(data, trak_start + 8, trak_end, "mdia");
            if (mdia != missing)
            {
                isobmff_box_header mdia_header{};
                if (!try_read_box_header(data.data(), mdia, trak_end, mdia_header)) {
                    set_error(context, "Malformed mdia box.");
                    return false;
                }
                const size_t mdia_end = mdia + mdia_header.size;
                const size_t hdlr = find_child_box(data, mdia + 8, mdia_end, "hdlr");
                if (hdlr != missing && hdlr + 20 <= mdia_end)
                {
                    const bool meta_handler = data[hdlr + 16] == 'm' && data[hdlr + 17] == 'e'
                        && data[hdlr + 18] == 't' && data[hdlr + 19] == 'a';
                    if (meta_handler)
                    {
                        bool found = false;
                        for (size_t i = 0; i < fragment_count; i++)
                        {
                            if (key_fragments[i] == nullptr) continue;
                            size_t frag_len = std::strlen(key_fragments[i]);
                            if (frag_len > 0)
                            {
                                auto it = std::search(data.begin() + trak_start, data.begin() + trak_end,
                                    key_fragments[i], key_fragments[i] + frag_len);
                                if (it != data.begin() + trak_end)
                                {
                                    std::string fp = lpb::crypto::compute_metadata_track_fingerprint("meta", key_fragments[i]);
                                    if (actions && action_count > 0)
                                    {
                                        bool authorized = false;
                                        for (size_t a = 0; a < action_count; ++a)
                                        {
                                            if (actions[a].artifact_role == LPB_ARTIFACT_MOTION_VIDEO &&
                                                actions[a].structure_kind == LPB_RESIDUE_QUICKTIME_METADATA_TRACK &&
                                                actions[a].selector == std::string_view(key_fragments[i]))
                                            {
                                                if (expected_protocol != LPB_SOURCE_PROTOCOL_UNKNOWN &&
                                                    actions[a].owner_protocol != expected_protocol)
                                                {
                                                    set_error(context, "Residue owner protocol mismatch for metadata track.");
                                                    return false;
                                                }
                                                if (actions[a].coordinate_space != LPB_COORD_STRUCTURED_SELECTOR)
                                                {
                                                    set_error(context, "Residue coordinate space mismatch for metadata track.");
                                                    return false;
                                                }
                                                if (actions[a].expected_fingerprint[0] == '\0' || fp != actions[a].expected_fingerprint)
                                                {
                                                    set_error(context, "Residue fingerprint missing or mismatch for metadata track.");
                                                    return false;
                                                }
                                                authorized = true;
                                                break;
                                            }
                                        }
                                        if (!authorized)
                                        {
                                            set_error(context, "Unauthorized removal of metadata track.");
                                            return false;
                                        }
                                    }
                                    else if (expected_protocol != LPB_SOURCE_PROTOCOL_UNKNOWN)
                                    {
                                        set_error(context, "Unauthorized removal of metadata track: missing action authority.");
                                        return false;
                                    }
                                    found = true;
                                    if (out_matched && i < out_matched->size()) (*out_matched)[i] = true;
                                    if (out_fps && i < out_fps->size()) (*out_fps)[i] = fp;
                                }
                            }
                        }
                        if (found)
                        {
                            remove_pos.push_back(pos);
                        }
                    }
                }
            }
        }
        pos += size;
    }

    if (remove_pos.empty())
    {
        out_new_moov.assign(data.begin(), data.end());
        return true;
    }

    out_modified = true;
    out_new_moov.clear();
    out_new_moov.reserve(moov_size);
    out_new_moov.insert(out_new_moov.end(), 4, 0); // size placeholder
    out_new_moov.insert(out_new_moov.end(), { 'm', 'o', 'o', 'v' });

    pos = 8;
    while (pos < moov_end)
    {
        isobmff_box_header child{};
        if (!try_read_box_header(data.data(), pos, moov_end, child)) {
            set_error(context, "Malformed moov child box during rebuild.");
            return false;
        }
        const size_t size = child.size;

        if (std::find(remove_pos.begin(), remove_pos.end(), pos) == remove_pos.end())
        {
            out_new_moov.insert(out_new_moov.end(), data.data() + pos, data.data() + pos + size);
        }
        pos += size;
    }

    if (out_new_moov.size() > moov_size || out_new_moov.size() > std::numeric_limits<uint32_t>::max()) {
        set_error(context, "Rebuilt moov is larger than the source box.");
        return false;
    }
    write_be32(out_new_moov.data(), static_cast<int32_t>(out_new_moov.size()));
    return true;
}

static bool rebuild_moov_without_matching_mdta_keys(
    lpb_context* context,
    std::span<const uint8_t> data,
    const char* const* name_starts,
    size_t name_starts_count,
    const char* const* name_contains,
    size_t name_contains_count,
    const char* const* value_contains,
    size_t value_contains_count,
    std::vector<uint8_t>& out_new_moov,
    bool& out_modified,
    std::vector<bool>* out_starts_matched = nullptr,
    std::vector<bool>* out_contains_matched = nullptr,
    std::vector<std::string>* out_starts_fps = nullptr,
    std::vector<std::string>* out_contains_fps = nullptr,
    const lpb_cleanup_action* actions = nullptr,
    size_t action_count = 0,
    lpb_source_protocol expected_protocol = LPB_SOURCE_PROTOCOL_UNKNOWN)
{
    out_modified = false;
    if (data.size() < 8 || !is_type(data, 0, "moov")) return false;
    isobmff_box_header moov_header{};
    if (!try_read_box_header(data.data(), 0, data.size(), moov_header) || moov_header.size != data.size()) {
        set_error(context, "Malformed moov box.");
        return false;
    }
    const size_t moov_size = moov_header.size;
    const size_t moov_end = moov_size;
    const size_t missing = std::numeric_limits<size_t>::max();

    bool meta_under_udta = false;
    size_t meta = missing;
    const size_t direct_meta = find_child_box(data, 8, moov_end, "meta");
    if (direct_meta != missing) {
        meta = direct_meta;
    }
    const size_t udta = find_child_box(data, 8, moov_end, "udta");
    size_t udta_end = moov_end;
    if (udta != missing) {
        isobmff_box_header udta_header{};
        if (!try_read_box_header(data.data(), udta, moov_end, udta_header)) {
            set_error(context, "Malformed QuickTime udta box.");
            return false;
        }
        udta_end = udta + udta_header.size;
    }
    if (meta == missing && udta != missing) {
        meta = find_child_box(data, udta + 8, udta_end, "meta");
        meta_under_udta = true;
    }
    if (meta == missing) {
        out_new_moov.assign(data.begin(), data.end());
        return true;
    }

    isobmff_box_header meta_header{};
    if (!try_read_box_header(data.data(), meta, meta_under_udta ? udta_end : moov_end, meta_header)) {
        set_error(context, "Malformed QuickTime metadata box.");
        return false;
    }
    const size_t meta_end = meta + meta_header.size;
    const size_t meta_children_start = get_meta_children_start(data, meta, meta_end);

    const size_t keys = find_child_box(data, meta_children_start, meta_end, "keys");
    const size_t ilst = find_child_box(data, meta_children_start, meta_end, "ilst");
    if (keys == missing || ilst == missing) {
        out_new_moov.assign(data.begin(), data.end());
        return true;
    }

    isobmff_box_header keys_header{};
    isobmff_box_header ilst_header{};
    if (!try_read_box_header(data.data(), keys, meta_end, keys_header) ||
        !try_read_box_header(data.data(), ilst, meta_end, ilst_header)) {
        set_error(context, "Malformed QuickTime keys/ilst box.");
        return false;
    }
    if (keys_header.size < 16 || ilst_header.size < 8) {
        set_error(context, "QuickTime keys/ilst box is truncated.");
        return false;
    }
    const size_t keys_end = keys + keys_header.size;
    const size_t ilst_end = ilst + ilst_header.size;

    std::vector<box_entry> key_entries;
    int32_t key_count = read_be32(data.data() + keys + 12);
    if (key_count < 0) {
        set_error(context, "QuickTime keys count is invalid.");
        return false;
    }
    size_t p = keys + 16;
    for (int32_t i = 0; i < key_count && p <= keys_end && keys_end - p >= 8; i++) {
        int32_t entry_size = read_be32(data.data() + p);
        if (entry_size < 8 || static_cast<size_t>(entry_size) > keys_end - p) {
            set_error(context, "QuickTime key entry exceeds its keys box.");
            return false;
        }
        key_entries.push_back({ p, static_cast<size_t>(entry_size), read_key_name(data.data(), p, static_cast<size_t>(entry_size)) });
        p += static_cast<size_t>(entry_size);
    }
    if (static_cast<size_t>(key_count) != key_entries.size() || p != keys_end) {
        set_error(context, "QuickTime keys box is truncated or has trailing bytes.");
        return false;
    }

    std::vector<ilst_item> ilst_items;
    size_t ip = ilst + 8;
    while (ip <= ilst_end && ilst_end - ip >= 8) {
        int32_t item_size = read_be32(data.data() + ip);
        if (item_size < 12 || static_cast<size_t>(item_size) > ilst_end - ip) {
            set_error(context, "QuickTime ilst item exceeds its ilst box.");
            return false;
        }
        int32_t index = read_be32(data.data() + ip + 4);
        std::string value = read_ilst_value(data.data(), ip + 8, ip + static_cast<size_t>(item_size));
        ilst_items.push_back({ ip, static_cast<size_t>(item_size), index, value });
        ip += static_cast<size_t>(item_size);
    }
    if (ip != ilst_end) {
        set_error(context, "QuickTime ilst box has trailing malformed bytes.");
        return false;
    }

    std::vector<bool> remove_key(key_entries.size(), false);
    bool any_removed = false;

    for (size_t i = 0; i < key_entries.size(); i++) {
        int32_t item_pos = -1;
        if (i < ilst_items.size()) {
            int32_t idx = ilst_items[i].index;
            item_pos = (idx >= 1 && idx <= static_cast<int32_t>(ilst_items.size()) && ilst_items[idx - 1].index == idx) ? idx - 1 : static_cast<int32_t>(i);
        }
        std::string value = item_pos >= 0 ? ilst_items[item_pos].value : "";
        std::string name = key_entries[i].name;

        bool should_remove = false;
        for (size_t k = 0; k < name_starts_count; k++) {
            if (starts_with_icase(name, name_starts[k])) {
                std::string fp = lpb::crypto::compute_mdta_key_fingerprint(name, reinterpret_cast<const uint8_t*>(value.data()), value.size());
                if (actions && action_count > 0) {
                    bool authorized = false;
                    for (size_t a = 0; a < action_count; ++a) {
                        if (actions[a].artifact_role == LPB_ARTIFACT_MOTION_VIDEO &&
                            actions[a].structure_kind == LPB_RESIDUE_QUICKTIME_MDTA_KEY &&
                            (name == actions[a].selector || starts_with_icase(name, actions[a].selector))) {
                            if (expected_protocol != LPB_SOURCE_PROTOCOL_UNKNOWN &&
                                actions[a].owner_protocol != expected_protocol) {
                                set_error(context, "Residue owner protocol mismatch for mdta key.");
                                return false;
                            }
                            if (actions[a].coordinate_space != LPB_COORD_STRUCTURED_SELECTOR) {
                                set_error(context, "Residue coordinate space mismatch for mdta key.");
                                return false;
                            }
                            if (actions[a].expected_fingerprint[0] == '\0' || fp != actions[a].expected_fingerprint) {
                                set_error(context, "Residue fingerprint missing or mismatch for mdta key.");
                                return false;
                            }
                            authorized = true;
                            break;
                        }
                    }
                    if (!authorized) {
                        set_error(context, "Unauthorized removal of mdta key.");
                        return false;
                    }
                } else if (expected_protocol != LPB_SOURCE_PROTOCOL_UNKNOWN) {
                    set_error(context, "Unauthorized removal of mdta key: missing action authority.");
                    return false;
                }
                should_remove = true;
                if (out_starts_matched && k < out_starts_matched->size()) (*out_starts_matched)[k] = true;
                if (out_starts_fps && k < out_starts_fps->size()) (*out_starts_fps)[k] = fp;
            }
        }
        for (size_t k = 0; k < name_contains_count; k++) {
            if (contains_icase(name, name_contains[k])) {
                std::string fp = lpb::crypto::compute_mdta_key_fingerprint(name, reinterpret_cast<const uint8_t*>(value.data()), value.size());
                if (actions && action_count > 0) {
                    bool authorized = false;
                    for (size_t a = 0; a < action_count; ++a) {
                        if (actions[a].artifact_role == LPB_ARTIFACT_MOTION_VIDEO &&
                            actions[a].structure_kind == LPB_RESIDUE_QUICKTIME_MDTA_KEY &&
                            (name == actions[a].selector || contains_icase(name, actions[a].selector))) {
                            if (expected_protocol != LPB_SOURCE_PROTOCOL_UNKNOWN &&
                                actions[a].owner_protocol != expected_protocol) {
                                set_error(context, "Residue owner protocol mismatch for mdta key.");
                                return false;
                            }
                            if (actions[a].coordinate_space != LPB_COORD_STRUCTURED_SELECTOR) {
                                set_error(context, "Residue coordinate space mismatch for mdta key.");
                                return false;
                            }
                            if (actions[a].expected_fingerprint[0] == '\0' || fp != actions[a].expected_fingerprint) {
                                set_error(context, "Residue fingerprint missing or mismatch for mdta key.");
                                return false;
                            }
                            authorized = true;
                            break;
                        }
                    }
                    if (!authorized) {
                        set_error(context, "Unauthorized removal of mdta key.");
                        return false;
                    }
                } else if (expected_protocol != LPB_SOURCE_PROTOCOL_UNKNOWN) {
                    set_error(context, "Unauthorized removal of mdta key: missing action authority.");
                    return false;
                }
                should_remove = true;
                if (out_contains_matched && k < out_contains_matched->size()) (*out_contains_matched)[k] = true;
                if (out_contains_fps && k < out_contains_fps->size()) (*out_contains_fps)[k] = fp;
            }
        }
        for (size_t k = 0; k < value_contains_count && !should_remove; k++) {
            if (contains_icase(value, value_contains[k])) should_remove = true;
        }

        if (should_remove) {
            remove_key[i] = true;
            any_removed = true;
        }
    }

    if (!any_removed) {
        out_new_moov.assign(data.begin(), data.end());
        return true;
    }

    std::vector<int32_t> remapped_key_indices(key_entries.size(), 0);
    int32_t new_index = 1;
    int32_t kept_keys = 0;
    for (size_t i = 0; i < key_entries.size(); i++) {
        if (!remove_key[i]) {
            remapped_key_indices[i] = new_index++;
            kept_keys++;
        }
    }

    size_t new_keys_payload_size = 0;
    for (size_t i = 0; i < key_entries.size(); i++) {
        if (!remove_key[i]) {
            if (key_entries[i].size > std::numeric_limits<size_t>::max() - new_keys_payload_size) {
                set_error(context, "QuickTime keys payload size overflows.");
                return false;
            }
            new_keys_payload_size += key_entries[i].size;
        }
    }

    if (new_keys_payload_size > std::numeric_limits<uint32_t>::max() - 16) {
        set_error(context, "QuickTime rebuilt keys box exceeds its 32-bit size field.");
        return false;
    }
    std::vector<uint8_t> new_keys;
    new_keys.reserve(16 + new_keys_payload_size);
    new_keys.insert(new_keys.end(), 16, 0);
    write_be32(new_keys.data(), static_cast<int32_t>(16 + new_keys_payload_size));
    new_keys[4] = 'k'; new_keys[5] = 'e'; new_keys[6] = 'y'; new_keys[7] = 's';
    write_be32(new_keys.data() + 12, kept_keys);
    for (size_t i = 0; i < key_entries.size(); i++) {
        if (remove_key[i]) continue;
        new_keys.insert(new_keys.end(), data.data() + key_entries[i].start, data.data() + key_entries[i].start + key_entries[i].size);
    }

    size_t total_ilst_size = 0;
    for (const auto& item : ilst_items) {
        if (item.index <= 0 || static_cast<size_t>(item.index) > remove_key.size()) {
            set_error(context, "QuickTime ilst item references an unknown key index.");
            return false;
        }
        if (remove_key[static_cast<size_t>(item.index) - 1]) continue;
        if (item.size > std::numeric_limits<size_t>::max() - total_ilst_size) {
            set_error(context, "QuickTime ilst size overflows.");
            return false;
        }
        total_ilst_size += item.size;
    }

    if (total_ilst_size > std::numeric_limits<uint32_t>::max() - 8) {
        set_error(context, "QuickTime rebuilt ilst box exceeds its 32-bit size field.");
        return false;
    }
    std::vector<uint8_t> new_ilst;
    new_ilst.reserve(8 + total_ilst_size);
    new_ilst.insert(new_ilst.end(), 8, 0);
    write_be32(new_ilst.data(), static_cast<int32_t>(8 + total_ilst_size));
    new_ilst[4] = 'i'; new_ilst[5] = 'l'; new_ilst[6] = 's'; new_ilst[7] = 't';
    for (const auto& item : ilst_items) {
        const size_t old_index = static_cast<size_t>(item.index) - 1;
        if (remove_key[old_index]) continue;
        size_t out_pos = new_ilst.size();
        new_ilst.insert(new_ilst.end(), data.data() + item.start, data.data() + item.start + item.size);
        write_be32(new_ilst.data() + out_pos + 4, remapped_key_indices[old_index]);
    }

    std::vector<uint8_t> new_meta = rebuild_container(data, meta, meta_end, meta_children_start, &new_keys, keys, &new_ilst, ilst);
    if (new_meta.empty()) {
        set_error(context, "Failed to rebuild QuickTime metadata container.");
        return false;
    }

    if (meta_under_udta) {
        const size_t rebuilt_udta_end = udta_end;
        std::vector<uint8_t> new_udta = rebuild_container(data, udta, rebuilt_udta_end, udta + 8, &new_meta, meta, nullptr, 0);
        if (new_udta.empty()) {
            set_error(context, "Failed to rebuild QuickTime udta container.");
            return false;
        }
        out_new_moov = rebuild_container(data, 0, moov_end, 8, &new_udta, udta, nullptr, 0);
    } else {
        out_new_moov = rebuild_container(data, 0, moov_end, 8, &new_meta, meta, nullptr, 0);
    }
    if (out_new_moov.empty() || out_new_moov.size() > moov_size) {
        set_error(context, "Rebuilt QuickTime moov is invalid or larger than the source box.");
        return false;
    }

    out_modified = true;
    return true;
}

extern "C" lpb_result LPB_CALL lpb_mp4_strip_stsd_tracks(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    const char** key_fragments,
    size_t fragment_count,
    uint8_t* output,
    size_t output_size,
    size_t* out_written)
{
    if (context == nullptr || input == nullptr || out_written == nullptr)
        return LPB_RESULT_INVALID_ARGUMENT;

    std::span<const uint8_t> data(input, input_size);
    const size_t missing = std::numeric_limits<size_t>::max();
    const size_t moov = find_top_level_box(data, "moov");
    if (moov == missing)
    {
        *out_written = 0;
        return LPB_RESULT_OK;
    }
    if (!has_complete_top_level_boxes(data.data(), data.size())) {
        set_error(context, "Input video contains a malformed top-level ISO-BMFF box.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    isobmff_box_header moov_header{};
    if (!try_read_box_header(data.data(), moov, data.size(), moov_header)) {
        set_error(context, "Malformed moov box.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const size_t moov_size = moov_header.size;
    const size_t moov_end = moov + moov_size;

    std::vector<uint8_t> new_moov;
    bool modified = false;
    if (!rebuild_moov_without_matching_tracks(context, data.subspan(moov, moov_size), key_fragments, fragment_count, new_moov, modified)) {
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (!modified) {
        *out_written = 0;
        return LPB_RESULT_OK;
    }

    size_t removed_bytes = moov_size - new_moov.size();
    std::vector<uint8_t> result;
    result.reserve(data.size() - removed_bytes);
    result.insert(result.end(), data.data(), data.data() + moov);
    result.insert(result.end(), new_moov.begin(), new_moov.end());
    result.insert(result.end(), data.data() + moov_end, data.data() + data.size());

    if (removed_bytes > 0)
    {
        if (!adjust_chunk_offsets(result, moov, moov, removed_bytes))
        {
            set_error(context, "Unable to safely relocate MP4 chunk offsets after track removal.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
    }

    return copy_output(context, result, output, output_size, out_written);
}

extern "C" lpb_result LPB_CALL lpb_mp4_strip_mdta_keys(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    const char** name_starts,
    size_t name_starts_count,
    const char** name_contains,
    size_t name_contains_count,
    const char** value_contains,
    size_t value_contains_count,
    uint8_t* output,
    size_t output_size,
    size_t* out_written)
{
    if (context == nullptr || input == nullptr || out_written == nullptr)
        return LPB_RESULT_INVALID_ARGUMENT;

    std::span<const uint8_t> data(input, input_size);
    const size_t missing = std::numeric_limits<size_t>::max();
    const size_t moov = find_top_level_box(data, "moov");
    if (moov == missing) { *out_written = 0; return LPB_RESULT_OK; }
    if (!has_complete_top_level_boxes(data.data(), data.size())) {
        set_error(context, "Input video contains a malformed top-level ISO-BMFF box.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    isobmff_box_header moov_header{};
    if (!try_read_box_header(data.data(), moov, data.size(), moov_header)) {
        set_error(context, "Malformed moov box.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const size_t moov_size = moov_header.size;
    const size_t moov_end = moov + moov_size;

    std::vector<uint8_t> new_moov;
    bool modified = false;
    if (!rebuild_moov_without_matching_mdta_keys(context, data.subspan(moov, moov_size),
            name_starts, name_starts_count,
            name_contains, name_contains_count,
            value_contains, value_contains_count,
            new_moov, modified)) {
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (!modified) {
        *out_written = 0;
        return LPB_RESULT_OK;
    }

    size_t removed_bytes = moov_size - new_moov.size();
    std::vector<uint8_t> result;
    result.reserve(data.size() - removed_bytes);
    result.insert(result.end(), data.data(), data.data() + moov);
    result.insert(result.end(), new_moov.begin(), new_moov.end());
    result.insert(result.end(), data.data() + moov_end, data.data() + data.size());

    if (removed_bytes > 0) {
        if (!adjust_chunk_offsets(result, moov, moov, removed_bytes))
        {
            set_error(context, "Unable to safely relocate MP4 chunk offsets after metadata removal.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
    }

    return copy_output(context, result, output, output_size, out_written);
}

namespace lpb::containers {

lpb_result stream_clean_mp4_bytes(
    lpb_context* context,
    std::span<const uint8_t> in_bytes,
    const std::string& out_path,
    const Mp4StripSpec& spec,
    Mp4StripOutcome& outcome)
{
    outcome = {};
    outcome.mdta_starts_matched.assign(spec.mdta_starts_count, false);
    outcome.mdta_contains_matched.assign(spec.mdta_contains_count, false);
    outcome.track_patterns_matched.assign(spec.track_patterns_count, false);
    outcome.mdta_starts_fingerprints.assign(spec.mdta_starts_count, "");
    outcome.mdta_contains_fingerprints.assign(spec.mdta_contains_count, "");
    outcome.track_fingerprints.assign(spec.track_patterns_count, "");

    const uint64_t file_size = in_bytes.size();
    if (file_size < 16) {
        set_error(context, "Input video file is too small.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    struct BoxEntry {
        uint64_t offset = 0;
        uint64_t size = 0;
        char type[4]{};
        bool is_target_uuid = false;
    };
    std::vector<BoxEntry> boxes;
    uint64_t pos = 0;
    uint64_t mdat_offset = std::numeric_limits<uint64_t>::max();
    size_t moov_index = std::numeric_limits<size_t>::max();

    while (pos < file_size) {
        if (pos + 8 > file_size) {
            set_error(context, "Failed to read ISO-BMFF box header.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        const uint8_t* hdr = in_bytes.data() + pos;
        uint32_t s32 = read_be32u(hdr);
        uint64_t s = s32;
        size_t hdr_sz = 8;
        if (s32 == 1) {
            if (pos + 16 > file_size) {
                set_error(context, "Failed to read extended ISO-BMFF box size.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            int64_t s64 = read_be64(hdr + 8);
            if (s64 < 16) {
                set_error(context, "Invalid 64-bit ISO-BMFF box size.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            s = static_cast<uint64_t>(s64);
            hdr_sz = 16;
        } else if (s32 == 0) {
            s = file_size - pos;
        }

        if (s < hdr_sz || s > file_size - pos) {
            set_error(context, "Malformed top-level ISO-BMFF box size.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        BoxEntry b{};
        b.offset = pos;
        b.size = s;
        std::memcpy(b.type, hdr + 4, 4);

        if (std::memcmp(b.type, "mdat", 4) == 0 && mdat_offset == std::numeric_limits<uint64_t>::max()) {
            mdat_offset = pos;
        }
        if (std::memcmp(b.type, "moov", 4) == 0 && moov_index == std::numeric_limits<size_t>::max()) {
            moov_index = boxes.size();
        }
        if (spec.strip_uuid_16 != nullptr && std::memcmp(b.type, "uuid", 4) == 0 && s >= hdr_sz + 16) {
            const uint8_t* uid = in_bytes.data() + pos + hdr_sz;
            if (std::memcmp(uid, spec.strip_uuid_16, 16) == 0) {
                std::vector<uint8_t> uid_payload(static_cast<size_t>(s - hdr_sz));
                std::memcpy(uid_payload.data(), uid, uid_payload.size());
                std::string ufp = lpb::crypto::compute_isobmff_box_fingerprint("uuid", s, uid_payload.data(), uid_payload.size());
                if (spec.actions && spec.action_count > 0) {
                    bool authorized = false;
                    for (size_t a = 0; a < spec.action_count; ++a) {
                        if (spec.actions[a].artifact_role == LPB_ARTIFACT_MOTION_VIDEO &&
                            spec.actions[a].structure_kind == LPB_RESIDUE_UUID_BOX) {
                            if (spec.expected_protocol != LPB_SOURCE_PROTOCOL_UNKNOWN &&
                                spec.actions[a].owner_protocol != spec.expected_protocol) {
                                set_error(context, "Residue owner protocol mismatch for uuid box.");
                                return LPB_RESULT_INVALID_ARGUMENT;
                            }
                            if (spec.actions[a].coordinate_space != LPB_COORD_STRUCTURED_SELECTOR) {
                                set_error(context, "Residue coordinate space mismatch for uuid box.");
                                return LPB_RESULT_INVALID_ARGUMENT;
                            }
                            if (spec.actions[a].expected_fingerprint[0] == '\0' || ufp != spec.actions[a].expected_fingerprint) {
                                set_error(context, "Residue fingerprint missing or mismatch for uuid box.");
                                return LPB_RESULT_INVALID_ARGUMENT;
                            }
                            authorized = true;
                            break;
                        }
                    }
                    if (!authorized) {
                        set_error(context, "Unauthorized removal of uuid box.");
                        return LPB_RESULT_INVALID_ARGUMENT;
                    }
                } else if (spec.expected_protocol != LPB_SOURCE_PROTOCOL_UNKNOWN) {
                    set_error(context, "Unauthorized removal of uuid box: missing action authority.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
                outcome.uuid_fingerprint = ufp;
                b.is_target_uuid = true;
                outcome.uuid_removed = true;
            }
        }
        boxes.push_back(b);
        pos += s;
    }

    if (pos != file_size) {
        set_error(context, "ISO-BMFF top-level boxes do not span entire file.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    // 2. Process moov box if present
    std::vector<uint8_t> moov_data;
    uint64_t moov_offset = 0;
    uint64_t old_moov_size = 0;

    if (moov_index != std::numeric_limits<size_t>::max()) {
        const auto& moov_box = boxes[moov_index];
        moov_offset = moov_box.offset;
        old_moov_size = moov_box.size;
        if (old_moov_size > 64 * 1024 * 1024) {
            set_error(context, "moov box exceeds maximum supported 64MB metadata size.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        moov_data.assign(in_bytes.data() + moov_offset, in_bytes.data() + moov_offset + old_moov_size);

        // 1. Strip MDTA keys if requested
        if (spec.mdta_starts_count > 0 || spec.mdta_contains_count > 0) {
            std::vector<uint8_t> new_moov;
            bool modified = false;
            if (!rebuild_moov_without_matching_mdta_keys(context, moov_data,
                    spec.mdta_starts, spec.mdta_starts_count,
                    spec.mdta_contains, spec.mdta_contains_count,
                    nullptr, 0, new_moov, modified,
                    &outcome.mdta_starts_matched, &outcome.mdta_contains_matched,
                    &outcome.mdta_starts_fingerprints, &outcome.mdta_contains_fingerprints,
                    spec.actions, spec.action_count, spec.expected_protocol)) {
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (modified) {
                moov_data = std::move(new_moov);
                outcome.mdta_removed = true;
            }
        }

        // 2. Strip STSD tracks if requested
        if (spec.track_patterns_count > 0) {
            std::vector<uint8_t> new_moov;
            bool modified = false;
            if (!rebuild_moov_without_matching_tracks(context, moov_data,
                    spec.track_patterns, spec.track_patterns_count,
                    new_moov, modified, &outcome.track_patterns_matched,
                    &outcome.track_fingerprints, spec.actions, spec.action_count, spec.expected_protocol)) {
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (modified) {
                moov_data = std::move(new_moov);
                outcome.track_removed = true;
            }
        }
    }

    auto p_out = utf8_to_path(out_path.c_str());
    std::error_code ec;
    auto temp_dir = p_out.parent_path();
    if (temp_dir.empty()) temp_dir = std::filesystem::current_path(ec);
    wchar_t temp_name[MAX_PATH]{};
    if (GetTempFileNameW(temp_dir.c_str(), L"lpb", 0, temp_name) == 0) {
        set_error(context, "Failed to create temporary output file.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    const std::filesystem::path temp(temp_name);

    if (!outcome.uuid_removed && !outcome.mdta_removed && !outcome.track_removed) {
        std::ofstream out(temp, std::ios::binary | std::ios::trunc);
        if (!out.is_open()) {
            std::filesystem::remove(temp, ec);
            set_error(context, "Failed to open temporary output file for direct copy.");
            return LPB_RESULT_INTERNAL_ERROR;
        }
        out.write(reinterpret_cast<const char*>(in_bytes.data()), static_cast<std::streamsize>(in_bytes.size()));
        out.flush();
        const bool write_ok = out.good();
        out.close();
        if (!write_ok || !MoveFileExW(temp.c_str(), p_out.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
            std::filesystem::remove(temp, ec);
            set_error(context, "Failed to publish unchanged video.");
            return LPB_RESULT_INTERNAL_ERROR;
        }
        return LPB_RESULT_OK;
    }

    // 3. Compute exact mdat shift and adjust chunk offsets
    if (moov_index != std::numeric_limits<size_t>::max() && mdat_offset != std::numeric_limits<uint64_t>::max()) {
        int64_t mdat_shift = 0;
        for (const auto& b : boxes) {
            if (b.is_target_uuid && b.offset < mdat_offset) {
                mdat_shift -= static_cast<int64_t>(b.size);
            }
        }
        if (moov_offset < mdat_offset) {
            int64_t moov_shrinkage = static_cast<int64_t>(old_moov_size) - static_cast<int64_t>(moov_data.size());
            mdat_shift -= moov_shrinkage;
        }
        if (mdat_shift != 0) {
            if (!shift_chunk_offsets(moov_data, 0, 0, mdat_shift)) {
                std::filesystem::remove(temp, ec);
                set_error(context, "Failed to adjust chunk offsets in moov.");
                return LPB_RESULT_INTERNAL_ERROR;
            }
        }
    }

    // 4. Stream-write to temporary file
    std::ofstream out(temp, std::ios::binary | std::ios::trunc);
    if (!out.is_open()) {
        std::filesystem::remove(temp, ec);
        set_error(context, "Failed to open temporary output file for streaming.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    for (size_t i = 0; i < boxes.size(); i++) {
        const auto& b = boxes[i];
        if (b.is_target_uuid) {
            continue; // strip target UUID box
        }
        if (i == moov_index) {
            out.write(reinterpret_cast<const char*>(moov_data.data()), static_cast<std::streamsize>(moov_data.size()));
            if (!out.good()) {
                out.close(); std::filesystem::remove(temp, ec);
                set_error(context, "Failed to write rebuilt moov.");
                return LPB_RESULT_INTERNAL_ERROR;
            }
            continue;
        }

        // Copy box from immutable buffer
        out.write(reinterpret_cast<const char*>(in_bytes.data() + b.offset), static_cast<std::streamsize>(b.size));
        if (!out.good()) {
            out.close(); std::filesystem::remove(temp, ec);
            set_error(context, "Failed to write box during copy.");
            return LPB_RESULT_INTERNAL_ERROR;
        }
    }

    out.flush();
    if (!out.good()) {
        out.close(); std::filesystem::remove(temp, ec);
        set_error(context, "Failed to flush cleaned output video.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    out.close();

    if (!MoveFileExW(temp.c_str(), p_out.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        std::filesystem::remove(temp, ec);
        set_error(context, "Failed to publish cleaned video file.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return LPB_RESULT_OK;
}

lpb_result stream_clean_mp4_file(
    lpb_context* context,
    const std::string& in_path,
    const std::string& out_path,
    const Mp4StripSpec& spec,
    Mp4StripOutcome& outcome)
{
    auto p_in = utf8_to_path(in_path.c_str());
    std::ifstream in(p_in, std::ios::binary | std::ios::ate);
    if (!in.is_open()) {
        set_error(context, "Failed to open input video for cleaning.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const auto file_size_signed = in.tellg();
    if (file_size_signed < 16) {
        set_error(context, "Input video file is too small.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    std::vector<uint8_t> bytes(static_cast<size_t>(file_size_signed));
    in.seekg(0, std::ios::beg);
    in.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    in.close();
    return stream_clean_mp4_bytes(context, bytes, out_path, spec, outcome);
}

bool mp4_get_mdta_key_fingerprint(
    std::span<const uint8_t> data,
    std::string_view target_key,
    std::string& out_fp)
{
    if (data.size() < 8 || target_key.empty()) return false;
    const size_t missing = std::numeric_limits<size_t>::max();
    size_t moov = missing;
    size_t p = 0;
    while (p + 8 <= data.size()) {
        isobmff_box_header box{};
        if (!try_read_box_header(data.data(), p, data.size(), box)) break;
        if (std::memcmp(data.data() + p + 4, "moov", 4) == 0) {
            moov = p;
            break;
        }
        p += box.size;
    }
    if (moov == missing || moov + 8 > data.size()) return false;

    isobmff_box_header moov_header{};
    if (!try_read_box_header(data.data(), moov, data.size(), moov_header)) return false;
    const size_t moov_end = moov + moov_header.size;

    size_t meta_start = missing;
    isobmff_box_header meta{};
    p = moov + moov_header.header_size;
    while (p < moov_end) {
        isobmff_box_header box{};
        if (!try_read_box_header(data.data(), p, moov_end, box)) break;
        if (std::memcmp(data.data() + p + 4, "meta", 4) == 0) {
            meta_start = p;
            meta = box;
            break;
        }
        if (std::memcmp(data.data() + p + 4, "udta", 4) == 0) {
            size_t up = p + box.header_size;
            size_t uend = p + box.size;
            while (up < uend) {
                isobmff_box_header ubox{};
                if (!try_read_box_header(data.data(), up, uend, ubox)) break;
                if (std::memcmp(data.data() + up + 4, "meta", 4) == 0) {
                    meta_start = up;
                    meta = ubox;
                    break;
                }
                up += ubox.size;
            }
            if (meta_start != missing) break;
        }
        p += box.size;
    }
    if (meta_start == missing || meta.size < meta.header_size + 8) return false;

    const size_t meta_end = meta_start + meta.size;
    size_t child_start = meta_start + meta.header_size;
    isobmff_box_header first_child{};
    if (!try_read_box_header(data.data(), child_start, meta_end, first_child)) {
        if (child_start <= meta_end - 4 && try_read_box_header(data.data(), child_start + 4, meta_end, first_child)) {
            child_start += 4;
        }
    }

    size_t keys_start = missing;
    isobmff_box_header keys{};
    size_t ilst_start = missing;
    isobmff_box_header ilst{};
    p = child_start;
    while (p < meta_end) {
        isobmff_box_header box{};
        if (!try_read_box_header(data.data(), p, meta_end, box)) break;
        if (std::memcmp(data.data() + p + 4, "keys", 4) == 0) {
            keys_start = p;
            keys = box;
        } else if (std::memcmp(data.data() + p + 4, "ilst", 4) == 0) {
            ilst_start = p;
            ilst = box;
        }
        p += box.size;
    }
    if (keys_start == missing || keys.size < keys.header_size + 8) return false;

    const size_t keys_body = keys_start + keys.header_size;
    const int32_t key_count = read_be32(data.data() + keys_body + 4);
    if (key_count <= 0 || key_count > 1024) return false;
    size_t key_pos = keys_body + 8;
    int32_t target_index = -1;
    for (uint32_t index = 1; index <= static_cast<uint32_t>(key_count); ++index) {
        if (key_pos > keys_start + keys.size || keys_start + keys.size - key_pos < 8) return false;
        const int32_t key_size = read_be32(data.data() + key_pos);
        if (key_size < 8 || static_cast<size_t>(key_size) > keys_start + keys.size - key_pos) return false;
        if (std::memcmp(data.data() + key_pos + 4, "mdta", 4) == 0) {
            std::string name = read_key_name(data.data(), key_pos, static_cast<size_t>(key_size));
            if (name == target_key) {
                target_index = static_cast<int32_t>(index);
                break;
            }
        }
        key_pos += static_cast<size_t>(key_size);
    }
    if (target_index < 0) return false;

    std::string val_str;
    if (ilst_start != missing && ilst.size >= 8) {
        size_t ip = ilst_start + 8;
        size_t ilst_end = ilst_start + ilst.size;
        while (ip <= ilst_end && ilst_end - ip >= 8) {
            int32_t item_size = read_be32(data.data() + ip);
            if (item_size < 8 || static_cast<size_t>(item_size) > ilst_end - ip) break;
            int32_t idx = read_be32(data.data() + ip + 4);
            if (idx == target_index) {
                val_str = read_ilst_value(data.data(), ip + 8, ip + static_cast<size_t>(item_size));
                break;
            }
            ip += static_cast<size_t>(item_size);
        }
    }

    out_fp = lpb::crypto::compute_mdta_key_fingerprint(
        target_key, reinterpret_cast<const uint8_t*>(val_str.data()), val_str.size());
    return true;
}

} // namespace lpb::containers
