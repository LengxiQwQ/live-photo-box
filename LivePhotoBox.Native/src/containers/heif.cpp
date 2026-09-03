#include "foundation/internal.h"
#include "binary/binary_io.h"
#include "containers/isobmff.h"
#include <string>

using namespace lpb;

namespace {
    bool find_box(binary_reader& reader, size_t end, const char* type, size_t& box_start, size_t& box_len, size_t& body_start) {
        if (end > reader.size()) end = reader.size();
        while (reader.position() <= end && end - reader.position() >= 8) {
            isobmff_box_header box{};
            if (!try_read_box_header(reader.data().data(), reader.position(), end, box)) return false;
            if (std::memcmp(reader.data().data() + box.start + 4, type, 4) == 0) {
                box_start = box.start;
                box_len = box.size;
                body_start = box.start + box.header_size;
                return true;
            }
            if (!reader.try_seek(box.start + box.size)) return false;
        }
        return false;
    }

    bool read_uint_local(binary_reader& reader, size_t size, uint64_t& value) {
        if (size > 8 || reader.remaining() < size) return false;
        value = 0;
        for (size_t i = 0; i < size; ++i) {
            uint8_t b = 0;
            if (!reader.try_read_u8(b)) return false;
            if (value > (std::numeric_limits<uint64_t>::max() >> 8)) return false;
            value = (value << 8) | b;
        }
        return true;
    }

    bool parse_iloc_for_item(binary_reader& reader, size_t iloc_body, size_t iloc_end,
        uint32_t target_item_id, uint64_t data_size, uint64_t* out_offset, uint64_t* out_length) {
        if (iloc_body > iloc_end || iloc_end > reader.size()) return false;
        if (!reader.try_seek(iloc_body)) return false;
        
        uint8_t iloc_version = 0;
        if (!reader.try_read_u8(iloc_version)) return false;
        if (!reader.skip(3)) return false; // flags
        
        uint8_t byte1 = 0, byte2 = 0;
        if (!reader.try_read_u8(byte1) || !reader.try_read_u8(byte2)) return false;
        
        uint8_t offset_size = (byte1 >> 4) & 0x0F;
        uint8_t length_size = byte1 & 0x0F;
        uint8_t base_offset_size = (byte2 >> 4) & 0x0F;
        uint8_t index_size = (iloc_version == 1 || iloc_version == 2) ? (byte2 & 0x0F) : 0;
        const auto valid_field_size = [](uint8_t size) noexcept { return size == 0 || size == 4 || size == 8; };
        if (iloc_version > 2 || !valid_field_size(offset_size) || !valid_field_size(length_size) ||
            !valid_field_size(base_offset_size) || !valid_field_size(index_size)) return false;
        
        uint32_t item_count = 0;
        if (iloc_version < 2) {
            uint16_t count16 = 0;
            if (!reader.try_read_be16u(count16)) return false;
            item_count = count16;
        } else {
            if (!reader.try_read_be32u(item_count)) return false;
        }

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
                uint16_t construction_method = 0;
                if (!reader.try_read_be16u(construction_method)) return false;
                construction_method &= 0x000F;
                if (item_id == target_item_id && construction_method != 0) return false;
            }
            uint16_t data_reference_index = 0;
            if (!reader.try_read_be16u(data_reference_index)) return false;
            if (item_id == target_item_id && data_reference_index != 0) return false;
            if (reader.position() > iloc_end) return false;
            
            uint64_t base_offset = 0;
            if (base_offset_size > 0) {
                if (!read_uint_local(reader, base_offset_size, base_offset)) return false;
            }
            
            uint16_t extent_count = 0;
            if (!reader.try_read_be16u(extent_count)) break;
            
            if (item_id == target_item_id && extent_count != 1) return false;
            for (uint16_t j = 0; j < extent_count; j++) {
                if ((iloc_version == 1 || iloc_version == 2) && index_size > 0) {
                    uint64_t ignored = 0;
                    if (!read_uint_local(reader, index_size, ignored)) return false;
                }
                
                uint64_t extent_offset = 0;
                if (offset_size > 0 && !read_uint_local(reader, offset_size, extent_offset)) return false;
                
                uint64_t extent_length = 0;
                if (length_size > 0 && !read_uint_local(reader, length_size, extent_length)) return false;

                if (item_id == target_item_id) {
                    if (extent_length == 0 || base_offset > data_size || extent_offset > data_size - base_offset ||
                        extent_length > data_size - base_offset - extent_offset) return false;
                    *out_offset = base_offset + extent_offset;
                    *out_length = extent_length;
                }
            }
            if (reader.position() > iloc_end) return false;
            if (item_id == target_item_id) return true;
        }
        return false;
    }
}

extern "C" LPB_API lpb_result LPB_CALL lpb_heif_locate_exif_item(
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
        if (!reader.skip(3)) return LPB_RESULT_INVALID_ARGUMENT;
        if (version > 1) return LPB_RESULT_INVALID_ARGUMENT;

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

        if (parse_iloc_for_item(reader, iloc_body, iloc_start + iloc_len,
            target_item_id, input_size, out_offset, out_length)) {
            return LPB_RESULT_OK;
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

extern "C" LPB_API lpb_result LPB_CALL lpb_heif_locate_xmp_item(
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

    try
    {
        binary_reader reader(input, input_size);
        size_t meta_start, meta_len, meta_body;
        if (!find_box(reader, input_size, "meta", meta_start, meta_len, meta_body)) {
            set_error(context, "No meta box found.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }

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

        if (!reader.try_seek(iinf_body)) return LPB_RESULT_INVALID_ARGUMENT;
        
        uint8_t version = 0;
        if (!reader.try_read_u8(version)) return LPB_RESULT_INVALID_ARGUMENT;
        if (!reader.skip(3)) return LPB_RESULT_INVALID_ARGUMENT;
        if (version > 1) return LPB_RESULT_INVALID_ARGUMENT;

        uint32_t count = 0;
        if (version == 0) {
            uint16_t count16 = 0;
            if (!reader.try_read_be16u(count16)) return LPB_RESULT_INVALID_ARGUMENT;
            count = count16;
        } else {
            if (!reader.try_read_be32u(count)) return LPB_RESULT_INVALID_ARGUMENT;
        }

        uint32_t target_item_id = 0xFFFFFFFF;
        bool found_xmp = false;
        
        for (uint32_t i = 0; i < count; i++) {
            size_t infe_start, infe_len, infe_body;
            if (!find_box(reader, iinf_start + iinf_len, "infe", infe_start, infe_len, infe_body)) {
                break;
            }
            
            binary_reader infe_reader(input, input_size);
            if (infe_reader.try_seek(infe_body)) {
                uint8_t infe_version = 0;
                if (infe_reader.try_read_u8(infe_version) && infe_reader.skip(3)) {
                    if (infe_version >= 2) {
                        uint32_t item_id = 0;
                        bool id_ok = false;
                        if (infe_version >= 3) {
                            id_ok = infe_reader.try_read_be32u(item_id);
                        } else {
                            uint16_t id16 = 0;
                            id_ok = infe_reader.try_read_be16u(id16);
                            item_id = id16;
                        }
                        
                        if (id_ok && infe_reader.skip(2)) { // skip item_protection_index
                            uint32_t item_type = 0;
                            if (infe_reader.try_read_be32u(item_type) && item_type == 0x6D696D65) { // 'mime'
                                auto read_infe_string = [&](std::string& value) {
                                    value.clear();
                                    while (infe_reader.position() < infe_start + infe_len) {
                                        uint8_t b = 0;
                                        if (!infe_reader.try_read_u8(b)) return false;
                                        if (b == 0) return true;
                                        if (value.size() >= 255) return false;
                                        value.push_back(static_cast<char>(b));
                                    }
                                    return false;
                                };
                                // Both forms occur in the field: some files
                                // omit the empty item_name, while Samsung puts
                                // an empty item_name before content_type.
                                std::string first_string;
                                if (!read_infe_string(first_string)) continue;
                                std::string type_str = first_string;
                                if (type_str.rfind("application/rdf+xml", 0) != 0 &&
                                    !read_infe_string(type_str)) continue;
                                if (type_str.find("application/rdf+xml") == 0) {
                                    target_item_id = item_id;
                                    found_xmp = true;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            reader.try_seek(infe_start + infe_len);
        }

        if (!found_xmp) {
            set_error(context, "No XMP item found in iinf.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        if (parse_iloc_for_item(reader, iloc_body, iloc_start + iloc_len,
            target_item_id, input_size, out_offset, out_length)) {
            return LPB_RESULT_OK;
        }

        set_error(context, "XMP item location not found in iloc.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    catch (const std::exception& ex)
    {
        set_error(context, ex.what());
        return LPB_RESULT_INVALID_ARGUMENT;
    }
}
