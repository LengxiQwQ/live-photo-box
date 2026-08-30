#include "foundation/internal.h"

lpb_result LPB_CALL lpb_heif_locate_exif_item(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    uint64_t* out_offset,
    uint64_t* out_length)
{
    if (context == nullptr || input == nullptr || out_offset == nullptr || out_length == nullptr)
    {
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    auto read_be16u = [](const uint8_t* p) -> uint16_t {
        return (static_cast<uint16_t>(p[0]) << 8) | p[1];
    };

    auto read_be32u_local = [](const uint8_t* p) -> uint32_t {
        return (static_cast<uint32_t>(p[0]) << 24) |
               (static_cast<uint32_t>(p[1]) << 16) |
               (static_cast<uint32_t>(p[2]) << 8) |
               static_cast<uint32_t>(p[3]);
    };
    
    auto read_be64u_local = [&read_be32u_local](const uint8_t* p) -> uint64_t {
        return (static_cast<uint64_t>(read_be32u_local(p)) << 32) | read_be32u_local(p + 4);
    };

    auto read_uint_local = [](const uint8_t* p, size_t size) -> uint64_t {
        uint64_t value = 0;
        for (size_t i = 0; i < size; ++i) {
            value = (value << 8) | p[i];
        }
        return value;
    };

    auto is_box_type = [](const uint8_t* p, const char* type) -> bool {
        return p[4] == static_cast<uint8_t>(type[0]) && 
               p[5] == static_cast<uint8_t>(type[1]) && 
               p[6] == static_cast<uint8_t>(type[2]) && 
               p[7] == static_cast<uint8_t>(type[3]);
    };

    auto find_box = [&read_be32u_local, &read_be64u_local, &is_box_type](
        const uint8_t* data, size_t start, size_t end, const char* type,
        size_t& box_start, size_t& box_len, size_t& body_start) -> bool {
        size_t p = start;
        while (p + 8 <= end) {
            uint64_t box_sz = read_be32u_local(data + p);
            size_t header = 8;
            if (box_sz == 1) {
                if (p + 16 > end) break;
                box_sz = read_be64u_local(data + p + 8);
                header = 16;
            } else if (box_sz == 0) {
                box_sz = end - p;
            }

            if (box_sz < header || p + box_sz > end) break;
            if (is_box_type(data + p, type)) {
                box_start = p;
                box_len = static_cast<size_t>(box_sz);
                body_start = p + header;
                return true;
            }
            p += static_cast<size_t>(box_sz);
        }
        return false;
    };

    try
    {
        size_t meta_start, meta_len, meta_body;
        if (!find_box(input, 0, input_size, "meta", meta_start, meta_len, meta_body)) {
            set_error(context, "No meta box found.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        // meta is FullBox: skip version(1) and flags(3)
        size_t child_start = meta_body + 4;
        size_t child_end = meta_start + meta_len;

        size_t iinf_start, iinf_len, iinf_body;
        if (!find_box(input, child_start, child_end, "iinf", iinf_start, iinf_len, iinf_body)) {
            set_error(context, "No iinf box found.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        size_t iloc_start, iloc_len, iloc_body;
        if (!find_box(input, child_start, child_end, "iloc", iloc_start, iloc_len, iloc_body)) {
            set_error(context, "No iloc box found.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        // Parse iinf to find Exif item_ID
        size_t p = iinf_body;
        size_t end = iinf_start + iinf_len;
        if (p + 4 > end) return LPB_RESULT_INVALID_ARGUMENT;
        uint8_t version = input[p];
        p += 4;
        
        uint32_t count = 0;
        if (version == 0) {
            if (p + 2 > end) return LPB_RESULT_INVALID_ARGUMENT;
            count = read_be16u(input + p);
            p += 2;
        } else {
            if (p + 4 > end) return LPB_RESULT_INVALID_ARGUMENT;
            count = read_be32u_local(input + p);
            p += 4;
        }

        uint32_t target_item_id = 0xFFFFFFFF;
        bool found_exif = false;
        
        for (uint32_t i = 0; i < count; i++) {
            if (p + 8 > end) break;
            size_t box_size = read_be32u_local(input + p);
            if (!is_box_type(input + p, "infe")) { p += box_size; continue; }
            if (box_size < 8 || p + box_size > end) break;
            
            size_t q = p + 8;
            uint8_t infe_version = input[q];
            uint32_t item_id = (infe_version >= 3) ? read_be32u_local(input + q + 4) : read_be16u(input + q + 4);
            size_t item_type_off = (infe_version >= 3) ? 10 : 8;
            
            if (q + item_type_off + 4 <= p + box_size) {
                if (input[q + item_type_off] == 'E' && input[q + item_type_off + 1] == 'x' && 
                    input[q + item_type_off + 2] == 'i' && input[q + item_type_off + 3] == 'f') {
                    target_item_id = item_id;
                    found_exif = true;
                    break;
                }
            }
            p += box_size;
        }

        if (!found_exif) {
            set_error(context, "No Exif item found in iinf.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        // Parse iloc to find location of target_item_id
        p = iloc_body;
        end = iloc_start + iloc_len;
        if (p + 6 > end) return LPB_RESULT_INVALID_ARGUMENT;
        
        version = input[p];
        p += 4;
        
        uint8_t offset_size = (input[p] >> 4) & 0x0F;
        uint8_t length_size = input[p] & 0x0F;
        uint8_t base_offset_size = (input[p+1] >> 4) & 0x0F;
        uint8_t index_size = input[p+1] & 0x0F;
        p += 2;
        
        count = 0;
        if (version < 2) {
            if (p + 2 > end) return LPB_RESULT_INVALID_ARGUMENT;
            count = read_be16u(input + p);
            p += 2;
        } else {
            if (p + 4 > end) return LPB_RESULT_INVALID_ARGUMENT;
            count = read_be32u_local(input + p);
            p += 4;
        }

        for (uint32_t i = 0; i < count; i++) {
            uint32_t item_id;
            if (version < 2) {
                if (p + 2 > end) break;
                item_id = read_be16u(input + p);
                p += 2;
            } else {
                if (p + 4 > end) break;
                item_id = read_be32u_local(input + p);
                p += 4;
            }
            
            uint8_t construction_method = 0;
            if (version == 1 || version == 2) {
                if (p + 2 > end) break;
                construction_method = static_cast<uint8_t>(read_be16u(input + p) & 0x000F);
                p += 2;
            }
            
            if (p + 2 > end) break;
            p += 2; // data_reference_index
            
            uint64_t base_offset = 0;
            if (base_offset_size > 0) {
                if (p + base_offset_size > end) break;
                base_offset = read_uint_local(input + p, base_offset_size);
                p += base_offset_size;
            }
            
            if (p + 2 > end) break;
            uint16_t extent_count = read_be16u(input + p);
            p += 2;
            
            uint64_t first_offset = 0, first_length = 0;
            for (uint16_t e = 0; e < extent_count; e++) {
                if ((version == 1 || version == 2) && index_size > 0) {
                    if (p + index_size > end) break;
                    p += index_size;
                }
                if (p + offset_size > end || p + length_size > end) break;
                
                uint64_t extent_offset = read_uint_local(input + p, offset_size);
                p += offset_size;
                uint64_t extent_length = read_uint_local(input + p, length_size);
                p += length_size;
                
                if (e == 0) {
                    first_offset = base_offset + extent_offset;
                    first_length = extent_length;
                }
            }
            
            if (item_id == target_item_id) {
                if (construction_method != 0) {
                    set_error(context, "Unsupported construction_method.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
                if (extent_count != 1) {
                    set_error(context, "Multiple extents not supported.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
                if (first_offset + first_length > input_size) {
                    set_error(context, "Exif item out of bounds.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
                *out_offset = first_offset;
                *out_length = first_length;
                return LPB_RESULT_OK;
            }
        }
        
        set_error(context, "Exif item location not found in iloc.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    catch (...)
    {
        set_error(context, "Unexpected failure while locating Exif item.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
}
