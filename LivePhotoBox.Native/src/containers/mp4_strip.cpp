#include "foundation/internal.h"
#include "containers/isobmff.h"

static bool is_printable_type(const uint8_t* p) {
    for (int i = 0; i < 4; i++) {
        if (p[i] < 0x20 || p[i] > 0x7E) return false;
    }
    return true;
}

static size_t get_meta_children_start(const std::vector<uint8_t>& data, size_t meta_start, size_t meta_end) {
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
    const std::vector<uint8_t>& data, size_t box_start, size_t box_end, size_t children_start,
    const std::vector<uint8_t>* replace_a, size_t replace_a_pos,
    const std::vector<uint8_t>* replace_b, size_t replace_b_pos)
{
    std::vector<uint8_t> result;
    result.reserve(box_end - box_start + 64);
    result.insert(result.end(), 4, 0);
    result.insert(result.end(), data.begin() + box_start + 4, data.begin() + box_start + 8);
    
    if (children_start == box_start + 12) {
        result.insert(result.end(), data.begin() + box_start + 8, data.begin() + box_start + 12);
    }
    
    size_t pos = children_start;
    while (pos + 8 <= box_end) {
        int32_t child_size = read_be32(data.data() + pos);
        if (child_size < 8 || pos + static_cast<size_t>(child_size) > box_end) break;
        
        if (pos == replace_a_pos && replace_a != nullptr) {
            result.insert(result.end(), replace_a->begin(), replace_a->end());
        }
        else if (pos == replace_b_pos && replace_b != nullptr) {
            result.insert(result.end(), replace_b->begin(), replace_b->end());
        }
        else {
            result.insert(result.end(), data.begin() + pos, data.begin() + pos + static_cast<size_t>(child_size));
        }
        pos += static_cast<size_t>(child_size);
    }
    
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
        if (!adjust_chunk_offsets(result, moov, targets.front().start, removed))
        {
            set_error(context, "Unable to safely relocate MP4 chunk offsets after UUID removal.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
    }

    return copy_output(context, result, output, output_size, out_written);
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

    std::vector<uint8_t> data(input, input + input_size);
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

    std::vector<size_t> remove_pos;
    size_t pos = moov + 8;
    while (pos + 8 <= moov_end)
    {
        isobmff_box_header child{};
        if (!try_read_box_header(data.data(), pos, moov_end, child)) {
            set_error(context, "Malformed moov child box.");
            return LPB_RESULT_INVALID_ARGUMENT;
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
                    return LPB_RESULT_INVALID_ARGUMENT;
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
                                    found = true;
                                    break;
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
        *out_written = 0;
        return LPB_RESULT_OK;
    }

    std::vector<uint8_t> new_moov;
    new_moov.reserve(moov_size);
    new_moov.insert(new_moov.end(), 4, 0); // size placeholder
    new_moov.insert(new_moov.end(), { 'm', 'o', 'o', 'v' });
    
    pos = moov + 8;
    while (pos < moov_end)
    {
        isobmff_box_header child{};
        if (!try_read_box_header(data.data(), pos, moov_end, child)) {
            set_error(context, "Malformed moov child box during rebuild.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        const size_t size = child.size;
        
        if (std::find(remove_pos.begin(), remove_pos.end(), pos) == remove_pos.end())
        {
            new_moov.insert(new_moov.end(), data.begin() + pos, data.begin() + pos + size);
        }
        pos += size;
    }
    
    if (new_moov.size() > moov_size || new_moov.size() > std::numeric_limits<uint32_t>::max()) {
        set_error(context, "Rebuilt moov is larger than the source box.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    write_be32(new_moov.data(), static_cast<int32_t>(new_moov.size()));
    
    size_t removed_bytes = moov_size - new_moov.size();
    std::vector<uint8_t> result;
    result.reserve(data.size() - removed_bytes);
    result.insert(result.end(), data.begin(), data.begin() + moov);
    result.insert(result.end(), new_moov.begin(), new_moov.end());
    result.insert(result.end(), data.begin() + moov_end, data.end());
    
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

    std::vector<uint8_t> data(input, input + input_size);
    if (input_size < 8 || !has_complete_top_level_boxes(data.data(), data.size())) {
        set_error(context, "Input video contains a malformed top-level ISO-BMFF box.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const size_t missing = std::numeric_limits<size_t>::max();
    const size_t moov = find_top_level_box(data, "moov");
    if (moov == missing) { *out_written = 0; return LPB_RESULT_OK; }
    isobmff_box_header moov_header{};
    if (!try_read_box_header(data.data(), moov, data.size(), moov_header)) {
        set_error(context, "Malformed moov box.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const size_t moov_size = moov_header.size;
    const size_t moov_end = moov + moov_size;

    bool meta_under_udta = false;
    size_t meta = missing;
    // Some Huawei files contain an ordinary udta/meta (without keys) and a
    // separate direct moov/meta carrying the mdta key table.  Selecting the
    // first meta by location silently skipped the protocol table.
    const size_t direct_meta = find_child_box(data, moov + 8, moov_end, "meta");
    if (direct_meta != missing) {
        meta = direct_meta;
    }
    const size_t udta = find_child_box(data, moov + 8, moov_end, "udta");
    size_t udta_end = moov_end;
    if (udta != missing) {
        isobmff_box_header udta_header{};
        if (!try_read_box_header(data.data(), udta, moov_end, udta_header)) {
            set_error(context, "Malformed QuickTime udta box.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        udta_end = udta + udta_header.size;
    }
    if (meta == missing && udta != missing) {
        meta = find_child_box(data, udta + 8, udta_end, "meta");
        meta_under_udta = true;
    }
    if (meta == missing) { *out_written = 0; return LPB_RESULT_OK; }

    isobmff_box_header meta_header{};
    if (!try_read_box_header(data.data(), meta, meta_under_udta ? udta_end : moov_end, meta_header)) {
        set_error(context, "Malformed QuickTime metadata box.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const size_t meta_end = meta + meta_header.size;
    const size_t meta_children_start = get_meta_children_start(data, meta, meta_end);

    const size_t keys = find_child_box(data, meta_children_start, meta_end, "keys");
    const size_t ilst = find_child_box(data, meta_children_start, meta_end, "ilst");
    if (keys == missing || ilst == missing) { *out_written = 0; return LPB_RESULT_OK; }

    isobmff_box_header keys_header{};
    isobmff_box_header ilst_header{};
    if (!try_read_box_header(data.data(), keys, meta_end, keys_header) ||
        !try_read_box_header(data.data(), ilst, meta_end, ilst_header)) {
        set_error(context, "Malformed QuickTime keys/ilst box.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (keys_header.size < 16 || ilst_header.size < 8) {
        set_error(context, "QuickTime keys/ilst box is truncated.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const size_t keys_end = keys + keys_header.size;
    const size_t ilst_end = ilst + ilst_header.size;

    std::vector<box_entry> key_entries;
    int32_t key_count = read_be32(data.data() + keys + 12);
    if (key_count < 0) {
        set_error(context, "QuickTime keys count is invalid.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    size_t p = keys + 16;
    for (int32_t i = 0; i < key_count && p <= keys_end && keys_end - p >= 8; i++) {
        int32_t entry_size = read_be32(data.data() + p);
        if (entry_size < 8 || static_cast<size_t>(entry_size) > keys_end - p) {
            set_error(context, "QuickTime key entry exceeds its keys box.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        key_entries.push_back({ p, static_cast<size_t>(entry_size), read_key_name(data.data(), p, static_cast<size_t>(entry_size)) });
        p += static_cast<size_t>(entry_size);
    }
    if (static_cast<size_t>(key_count) != key_entries.size() || p != keys_end) {
        set_error(context, "QuickTime keys box is truncated or has trailing bytes.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::vector<ilst_item> ilst_items;
    size_t ip = ilst + 8;
    while (ip <= ilst_end && ilst_end - ip >= 8) {
        int32_t item_size = read_be32(data.data() + ip);
        if (item_size < 12 || static_cast<size_t>(item_size) > ilst_end - ip) {
            set_error(context, "QuickTime ilst item exceeds its ilst box.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        int32_t index = read_be32(data.data() + ip + 4);
        std::string value = read_ilst_value(data.data(), ip + 8, ip + static_cast<size_t>(item_size));
        ilst_items.push_back({ ip, static_cast<size_t>(item_size), index, value });
        ip += static_cast<size_t>(item_size);
    }
    if (ip != ilst_end) {
        set_error(context, "QuickTime ilst box has trailing malformed bytes.");
        return LPB_RESULT_INVALID_ARGUMENT;
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
        for (size_t k = 0; k < name_starts_count && !should_remove; k++) {
            if (starts_with_icase(name, name_starts[k])) should_remove = true;
        }
        for (size_t k = 0; k < name_contains_count && !should_remove; k++) {
            if (contains_icase(name, name_contains[k])) should_remove = true;
        }
        for (size_t k = 0; k < value_contains_count && !should_remove; k++) {
            if (contains_icase(value, value_contains[k])) should_remove = true;
        }

        if (should_remove) {
            remove_key[i] = true;
            any_removed = true;
        }
    }

    if (!any_removed) { *out_written = 0; return LPB_RESULT_OK; }

    int32_t kept_keys = 0;
    size_t total_keys_size = 0;
    for (size_t i = 0; i < key_entries.size(); i++) {
        if (!remove_key[i]) { kept_keys++; total_keys_size += key_entries[i].size; }
    }

    std::vector<uint8_t> new_keys;
    new_keys.reserve(16 + total_keys_size);
    new_keys.insert(new_keys.end(), 16, 0);
    write_be32(new_keys.data(), static_cast<int32_t>(16 + total_keys_size));
    new_keys[4] = 'k'; new_keys[5] = 'e'; new_keys[6] = 'y'; new_keys[7] = 's';
    write_be32(new_keys.data() + 12, kept_keys);
    for (size_t i = 0; i < key_entries.size(); i++) {
        if (remove_key[i]) continue;
        new_keys.insert(new_keys.end(), data.begin() + key_entries[i].start, data.begin() + key_entries[i].start + key_entries[i].size);
    }

    size_t total_ilst_size = 0;
    for (size_t i = 0; i < ilst_items.size(); i++) {
        if (i < remove_key.size() && remove_key[i]) continue;
        total_ilst_size += ilst_items[i].size;
    }

    std::vector<uint8_t> new_ilst;
    new_ilst.reserve(8 + total_ilst_size);
    new_ilst.insert(new_ilst.end(), 8, 0);
    write_be32(new_ilst.data(), static_cast<int32_t>(8 + total_ilst_size));
    new_ilst[4] = 'i'; new_ilst[5] = 'l'; new_ilst[6] = 's'; new_ilst[7] = 't';
    int32_t new_index = 1;
    for (size_t i = 0; i < ilst_items.size(); i++) {
        if (i < remove_key.size() && remove_key[i]) continue;
        size_t out_pos = new_ilst.size();
        new_ilst.insert(new_ilst.end(), data.begin() + ilst_items[i].start, data.begin() + ilst_items[i].start + ilst_items[i].size);
        write_be32(new_ilst.data() + out_pos + 4, new_index++);
    }

    std::vector<uint8_t> new_meta = rebuild_container(data, meta, meta_end, meta_children_start, &new_keys, keys, &new_ilst, ilst);
    std::vector<uint8_t> new_moov;

    if (meta_under_udta) {
        const size_t rebuilt_udta_end = udta_end;
        std::vector<uint8_t> new_udta = rebuild_container(data, udta, rebuilt_udta_end, udta + 8, &new_meta, meta, nullptr, 0);
        new_moov = rebuild_container(data, moov, moov_end, moov + 8, &new_udta, udta, nullptr, 0);
    } else {
        new_moov = rebuild_container(data, moov, moov_end, moov + 8, &new_meta, meta, nullptr, 0);
    }

    size_t removed_bytes = moov_size - new_moov.size();
    std::vector<uint8_t> result;
    result.reserve(data.size() - removed_bytes);
    result.insert(result.end(), data.begin(), data.begin() + moov);
    result.insert(result.end(), new_moov.begin(), new_moov.end());
    result.insert(result.end(), data.begin() + moov_end, data.end());

    if (removed_bytes > 0) {
        if (!adjust_chunk_offsets(result, moov, moov, removed_bytes)) {
            set_error(context, "Unable to safely relocate MP4 chunk offsets after metadata removal.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
    }

    return copy_output(context, result, output, output_size, out_written);
}
