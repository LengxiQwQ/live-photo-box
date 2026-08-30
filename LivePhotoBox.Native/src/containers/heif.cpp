#include "foundation/internal.h"
#include "binary/binary_io.h"

using namespace lpb;

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

    auto find_box = [](binary_reader& reader, size_t end, const char* type, size_t& box_start, size_t& box_len, size_t& body_start) -> bool {
        while (reader.position() + 8 <= end) {
            box_start = reader.position();
            uint32_t box_sz = 0;
            if (!reader.try_read_be32u(box_sz)) break;
            
            size_t header_len = 8;
            uint64_t full_size = box_sz;
            
            if (box_sz == 1) {
                if (!reader.skip(4)) break; // skip type
                int64_t sz64 = 0;
                if (!reader.try_read_be64(sz64)) break;
                full_size = static_cast<uint64_t>(sz64);
                header_len = 16;
            } else if (box_sz == 0) {
                full_size = end - box_start;
            }
            
            if (full_size < header_len || box_start + full_size > end) break;
            
            reader.try_seek(box_start + 4);
            uint8_t type_buf[4];
            if (reader.try_read_bytes(type_buf, 4)) {
                if (type_buf[0] == type[0] && type_buf[1] == type[1] &&
                    type_buf[2] == type[2] && type_buf[3] == type[3]) {
                    box_len = static_cast<size_t>(full_size);
                    body_start = box_start + header_len;
                    return true;
                }
            }
            reader.try_seek(box_start + static_cast<size_t>(full_size));
        }
        return false;
    };

    try
    {
        binary_reader reader(input, input_size);
        size_t meta_start, meta_len, meta_body;
        if (!find_box(reader, input_size, "meta", meta_start, meta_len, meta_body)) {
            set_error(context, "No meta box found.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        // meta is FullBox: skip version(1) and flags(3)
        size_t child_start = meta_body + 4;
        size_t child_end = meta_start + meta_len;

        reader.try_seek(child_start);
        size_t iinf_start, iinf_len, iinf_body;
        if (!find_box(reader, child_end, "iinf", iinf_start, iinf_len, iinf_body)) {
            set_error(context, "No iinf box found.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        reader.try_seek(child_start);
        size_t iloc_start, iloc_len, iloc_body;
        if (!find_box(reader, child_end, "iloc", iloc_start, iloc_len, iloc_body)) {
            set_error(context, "No iloc box found.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        // Parse iinf to find Exif item_ID
        if (!reader.try_seek(iinf_body)) return LPB_RESULT_INVALID_ARGUMENT;
        
        uint8_t version = 0;
        if (!reader.try_read_u8(version)) return LPB_RESULT_INVALID_ARGUMENT;
        reader.skip(3); // skip flags

        uint32_t count = 0;
        if (version == 0) {
            uint16_t count16 = 0;
            if (!reader.try_read_be16u(count16)) return LPB_RESULT_INVALID_ARGUMENT;
            count = count16;
        } else {
            if (!reader.try_read_be32u(count)) return LPB_RESULT_INVALID_ARGUMENT;
        }

        uint32_t target_item_id = 0xFFFFFFFF;
        bool found_exif = false;
        
        for (uint32_t i = 0; i < count; i++) {
            size_t infe_start, infe_len, infe_body;
            if (!find_box(reader, iinf_start + iinf_len, "infe", infe_start, infe_len, infe_body)) {
                break;
            }
            
            binary_reader infe_reader(input, input_size);
            if (infe_reader.try_seek(infe_body)) {
                uint8_t infe_version = 0;
                if (infe_reader.try_read_u8(infe_version) && infe_reader.skip(3)) {
                    uint32_t item_id = 0;
                    bool id_ok = false;
                    if (infe_version >= 3) {
                        id_ok = infe_reader.try_read_be32u(item_id);
                    } else if (infe_version == 2) {
                        uint16_t id16 = 0;
                        id_ok = infe_reader.try_read_be16u(id16);
                        item_id = id16;
                    }
                    
                    if (id_ok && infe_reader.skip(2)) { // skip item_protection_index
                        uint32_t item_type = 0;
                        if (infe_reader.try_read_be32u(item_type) && item_type == 0x45786966) { // 'Exif'
                            target_item_id = item_id;
                            found_exif = true;
                            break;
                        }
                    }
                }
            }
            reader.try_seek(infe_start + infe_len);
        }

        if (!found_exif) {
            set_error(context, "No Exif item found in iinf.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        // Parse iloc to find location of target_item_id
        if (!reader.try_seek(iloc_body)) return LPB_RESULT_INVALID_ARGUMENT;
        
        uint8_t iloc_version = 0;
        if (!reader.try_read_u8(iloc_version)) return LPB_RESULT_INVALID_ARGUMENT;
        reader.skip(3); // flags
        
        uint8_t byte1 = 0, byte2 = 0;
        if (!reader.try_read_u8(byte1) || !reader.try_read_u8(byte2)) return LPB_RESULT_INVALID_ARGUMENT;
        
        uint8_t offset_size = (byte1 >> 4) & 0x0F;
        uint8_t length_size = byte1 & 0x0F;
        uint8_t base_offset_size = (byte2 >> 4) & 0x0F;
        uint8_t index_size = (iloc_version == 1 || iloc_version == 2) ? (byte2 & 0x0F) : 0;
        
        uint32_t item_count = 0;
        if (iloc_version < 2) {
            uint16_t count16 = 0;
            if (!reader.try_read_be16u(count16)) return LPB_RESULT_INVALID_ARGUMENT;
            item_count = count16;
        } else {
            if (!reader.try_read_be32u(item_count)) return LPB_RESULT_INVALID_ARGUMENT;
        }

        auto read_uint_local = [&reader](size_t size) -> uint64_t {
            uint64_t value = 0;
            for (size_t i = 0; i < size; ++i) {
                uint8_t b = 0;
                if (reader.try_read_u8(b)) value = (value << 8) | b;
            }
            return value;
        };

        for (uint32_t i = 0; i < item_count; i++) {
            uint32_t item_id = 0;
            if (iloc_version < 2) {
                uint16_t id16 = 0;
                if (!reader.try_read_be16u(id16)) break;
                item_id = id16;
            } else {
                if (!reader.try_read_be32u(item_id)) break;
            }
            
            if (iloc_version == 1 || iloc_version == 2) {
                reader.skip(2); // construction_method
            }
            reader.skip(2); // data_reference_index
            
            uint64_t base_offset = 0;
            if (base_offset_size > 0) {
                base_offset = read_uint_local(base_offset_size);
            }
            
            uint16_t extent_count = 0;
            if (!reader.try_read_be16u(extent_count)) break;
            
            for (uint16_t j = 0; j < extent_count; j++) {
                if ((iloc_version == 1 || iloc_version == 2) && index_size > 0) {
                    read_uint_local(index_size);
                }
                
                uint64_t extent_offset = 0;
                if (offset_size > 0) extent_offset = read_uint_local(offset_size);
                
                uint64_t extent_length = 0;
                if (length_size > 0) extent_length = read_uint_local(length_size);
                
                if (item_id == target_item_id) {
                    *out_offset = base_offset + extent_offset;
                    *out_length = extent_length;
                    
                    if (extent_length >= 4) {
                        binary_reader exif_reader(input, input_size);
                        if (exif_reader.try_seek(*out_offset)) {
                            uint32_t exif_hdr = 0;
                            if (exif_reader.try_read_be32u(exif_hdr) && exif_hdr != 0x45786966) {
                                // HEIF Exif metadata usually starts with a 4-byte offset
                                *out_offset += 4;
                                *out_length -= 4;
                            }
                        }
                    }
                    
                    return LPB_RESULT_OK;
                }
            }
        }

        set_error(context, "Exif item location not found in iloc.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    catch (const std::exception& ex)
    {
        set_error(context, ex.what());
        return LPB_RESULT_INVALID_ARGUMENT;
    }
}
