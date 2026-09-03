#include "foundation/internal.h"
#include "containers/isobmff.h"
#include "binary/binary_io.h"
#include <limits>

using namespace lpb;

bool try_read_box_header(
    const uint8_t* data,
    size_t start,
    size_t end,
    isobmff_box_header& out) noexcept
{
    if (!data || start > end || end - start < 8) return false;
    const uint32_t size32 = read_be32u(data + start);
    size_t header_size = 8;
    uint64_t size = size32;
    if (size32 == 1)
    {
        if (end - start < 16) return false;
        size = static_cast<uint64_t>(read_be64(data + start + 8));
        if (size < 16) return false;
        header_size = 16;
    }
    else if (size32 == 0)
    {
        size = end - start;
    }
    if (size < header_size || size > static_cast<uint64_t>(end - start)) return false;
    out = { start, static_cast<size_t>(size), header_size, size32, size32 == 0 };
    return true;
}

bool is_valid_isobmff_media_range(
    const uint8_t* data,
    size_t data_size,
    uint64_t offset,
    uint64_t length) noexcept
{
    if (!data || offset > data_size || length < 8 || length > data_size - static_cast<size_t>(offset)) return false;
    const size_t start = static_cast<size_t>(offset);
    const size_t end = start + static_cast<size_t>(length);
    size_t position = start;
    bool saw_ftyp = false;
    bool saw_mdat = false;
    bool saw_moov = false;
    bool first = true;
    while (position < end)
    {
        isobmff_box_header box{};
        if (!try_read_box_header(data, position, end, box)) return false;
        const uint8_t* type = data + position + 4;
        if (first)
        {
            if (std::memcmp(type, "ftyp", 4) != 0) return false;
            if (box.size < box.header_size + 8) return false;
            first = false;
        }
        if (std::memcmp(type, "ftyp", 4) == 0) saw_ftyp = true;
        else if (std::memcmp(type, "mdat", 4) == 0) saw_mdat = true;
        else if (std::memcmp(type, "moov", 4) == 0) saw_moov = true;
        position += box.size;
    }
    return position == end && saw_ftyp && saw_mdat && saw_moov;
}

bool is_type(const std::vector<uint8_t>& data, size_t offset, const char* type) noexcept
{
    binary_reader reader(data);
    if (!reader.try_seek(offset) || reader.remaining() < 8) return false;
    const uint8_t* p = reader.current_ptr();
    return p[4] == static_cast<uint8_t>(type[0]) &&
           p[5] == static_cast<uint8_t>(type[1]) &&
           p[6] == static_cast<uint8_t>(type[2]) &&
           p[7] == static_cast<uint8_t>(type[3]);
}

size_t find_child_box(
    const std::vector<uint8_t>& data,
    size_t start,
    size_t end,
    const char* type) noexcept
{
    binary_reader reader(data);
    if (!reader.try_seek(start)) return std::numeric_limits<size_t>::max();
    if (end > data.size()) end = data.size();

    while (reader.position() <= end && end - reader.position() >= 8)
    {
        const size_t position = reader.position();
        isobmff_box_header box{};
        if (!try_read_box_header(data.data(), position, end, box)) break;

        if (is_type(data, position, type))
        {
            return position;
        }
        
        if (!reader.try_seek(position + box.size)) break;
    }
    return std::numeric_limits<size_t>::max();
}

size_t find_top_level_box(const std::vector<uint8_t>& data, const char* type) noexcept
{
    return find_child_box(data, 0, data.size(), type);
}

bool adjust_trak_chunk_offsets(
    std::vector<uint8_t>& data,
    size_t trak_start,
    size_t trak_end,
    size_t threshold,
    size_t removed_bytes) noexcept
{
    const size_t missing = std::numeric_limits<size_t>::max();
    
    auto get_box_end = [&](size_t start, size_t end_limit) -> size_t {
        binary_reader reader(data);
        isobmff_box_header box{};
        if (!try_read_box_header(data.data(), start, end_limit, box)) return missing;
        return start + box.size;
    };

    const size_t mdia = find_child_box(data, trak_start + 8, trak_end, "mdia");
    if (mdia == missing) return true;
    const size_t mdia_end = get_box_end(mdia, data.size());
    if (mdia_end == missing) return false;

    const size_t minf = find_child_box(data, mdia + 8, mdia_end, "minf");
    if (minf == missing) return true;
    const size_t minf_end = get_box_end(minf, data.size());
    if (minf_end == missing) return false;

    const size_t stbl = find_child_box(data, minf + 8, minf_end, "stbl");
    if (stbl == missing) return true;
    const size_t stbl_end = get_box_end(stbl, data.size());
    if (stbl_end == missing) return false;

    const size_t stco = find_child_box(data, stbl + 8, stbl_end, "stco");
    if (stco != missing)
    {
        isobmff_box_header stco_box{};
        if (!try_read_box_header(data.data(), stco, stbl_end, stco_box) || stco_box.size < 16) return false;
        binary_reader reader(data);
        if (!reader.try_seek(stco + 12)) return false;
        uint32_t count = 0;
        if (!reader.try_read_be32u(count) || count > (stco_box.size - 16) / 4) return false;
        {
            binary_writer writer(data);
            for (uint32_t index = 0; index < count; ++index)
            {
                size_t field = stco + 16 + static_cast<size_t>(index) * 4;
                uint32_t offset = 0;
                if (!reader.try_seek(field) || !reader.try_read_be32u(offset)) return false;
                
                if (offset > 0 && static_cast<size_t>(offset) > threshold && removed_bytes <= offset)
                {
                    if (removed_bytes > std::numeric_limits<uint32_t>::max()) return false;
                    if (!writer.try_seek(field) || !writer.try_write_be32u(offset - static_cast<uint32_t>(removed_bytes))) return false;
                }
            }
        }
    }

    const size_t co64 = find_child_box(data, stbl + 8, stbl_end, "co64");
    if (co64 != missing)
    {
        isobmff_box_header co64_box{};
        if (!try_read_box_header(data.data(), co64, stbl_end, co64_box) || co64_box.size < 16) return false;
        binary_reader reader(data);
        if (!reader.try_seek(co64 + 12)) return false;
        uint32_t count = 0;
        if (!reader.try_read_be32u(count) || count > (co64_box.size - 16) / 8) return false;
        {
            binary_writer writer(data);
            for (uint32_t index = 0; index < count; ++index)
            {
                size_t field = co64 + 16 + static_cast<size_t>(index) * 8;
                int64_t offset = 0;
                if (!reader.try_seek(field) || !reader.try_read_be64(offset)) return false;
                
                if (offset > 0 && static_cast<uint64_t>(offset) > threshold && removed_bytes <= static_cast<uint64_t>(offset))
                {
                    if (removed_bytes > static_cast<size_t>(std::numeric_limits<int64_t>::max()) ||
                        !writer.try_seek(field) || !writer.try_write_be64(offset - static_cast<int64_t>(removed_bytes))) return false;
                }
            }
        }
    }
    return true;
}

bool adjust_chunk_offsets(
    std::vector<uint8_t>& data,
    size_t moov_start,
    size_t threshold,
    size_t removed_bytes) noexcept
{
    if (moov_start > data.size()) return false;
    isobmff_box_header moov_box{};
    if (!try_read_box_header(data.data(), moov_start, data.size(), moov_box)) return false;
    const size_t moov_end = moov_start + moov_box.size;
    size_t position = moov_start + moov_box.header_size;
    
    while (position < moov_end)
    {
        isobmff_box_header child{};
        if (!try_read_box_header(data.data(), position, moov_end, child)) return false;
        if (is_type(data, position, "trak"))
        {
            if (!adjust_trak_chunk_offsets(data, position, position + child.size, threshold, removed_bytes)) return false;
        }
        position += child.size;
    }
    return position == moov_end;
}

static bool shift_trak_chunk_offsets(
    std::vector<uint8_t>& data,
    size_t trak_start,
    size_t trak_end,
    size_t threshold,
    int64_t delta) noexcept
{
    const size_t missing = std::numeric_limits<size_t>::max();
    
    auto get_box_end = [&](size_t start, size_t end_limit) -> size_t {
        binary_reader reader(data);
        if (!reader.try_seek(start)) return missing;
        uint32_t sz = 0;
        if (!reader.try_read_be32u(sz) || sz < 8 || sz > end_limit - start) return missing;
        return start + sz;
    };

    const size_t mdia = find_child_box(data, trak_start + 8, trak_end, "mdia");
    if (mdia == missing) return true; // not a media track or no mdia
    const size_t mdia_end = get_box_end(mdia, data.size());
    if (mdia_end == missing) return false;

    const size_t minf = find_child_box(data, mdia + 8, mdia_end, "minf");
    if (minf == missing) return true;
    const size_t minf_end = get_box_end(minf, data.size());
    if (minf_end == missing) return false;

    const size_t stbl = find_child_box(data, minf + 8, minf_end, "stbl");
    if (stbl == missing) return true;
    const size_t stbl_end = get_box_end(stbl, data.size());
    if (stbl_end == missing) return false;

    const size_t stco = find_child_box(data, stbl + 8, stbl_end, "stco");
    if (stco != missing && stco + 16 <= stbl_end)
    {
        binary_reader reader(data);
        if (reader.try_seek(stco + 12)) {
            uint32_t count = 0;
            if (reader.try_read_be32u(count) && stco <= stbl_end && stbl_end - stco >= 16 &&
                count <= (stbl_end - stco - 16) / 4) {
                binary_writer writer(data);
                for (uint32_t index = 0; index < count; ++index)
                {
                    size_t field = stco + 16 + static_cast<size_t>(index) * 4;
                    if (field > stbl_end - 4) return false;
                    
                    uint32_t offset = 0;
                    reader.try_seek(field);
                    if (!reader.try_read_be32u(offset)) return false;
                    
                    if (offset > 0 && static_cast<size_t>(offset) > threshold)
                    {
                        if ((delta > 0 && offset > std::numeric_limits<int64_t>::max() - delta) ||
                            (delta < 0 && offset < std::numeric_limits<int64_t>::min() - delta)) return false;
                        int64_t shifted = static_cast<int64_t>(offset) + delta;
                        if (shifted <= 0 || shifted > static_cast<int64_t>(std::numeric_limits<uint32_t>::max())) {
                            return false; // underflow / overflow
                        }
                        writer.try_seek(field);
                        writer.try_write_be32u(static_cast<uint32_t>(shifted));
                    }
                }
            }
        }
    }

    const size_t co64 = find_child_box(data, stbl + 8, stbl_end, "co64");
    if (co64 != missing && co64 + 16 <= stbl_end)
    {
        binary_reader reader(data);
        if (reader.try_seek(co64 + 12)) {
            uint32_t count = 0;
            if (reader.try_read_be32u(count) && co64 <= stbl_end && stbl_end - co64 >= 16 &&
                count <= (stbl_end - co64 - 16) / 8) {
                binary_writer writer(data);
                for (uint32_t index = 0; index < count; ++index)
                {
                    size_t field = co64 + 16 + static_cast<size_t>(index) * 8;
                    if (field > stbl_end - 8) return false;
                    
                    int64_t offset = 0;
                    reader.try_seek(field);
                    if (!reader.try_read_be64(offset)) return false;
                    
                    if (offset > 0 && static_cast<uint64_t>(offset) > threshold)
                    {
                        if ((delta > 0 && offset > std::numeric_limits<int64_t>::max() - delta) ||
                            (delta < 0 && offset < std::numeric_limits<int64_t>::min() - delta)) return false;
                        int64_t shifted = offset + delta;
                        if (shifted <= 0) {
                            return false; // underflow
                        }
                        writer.try_seek(field);
                        writer.try_write_be64(shifted);
                    }
                }
            }
        }
    }

    return true;
}

bool shift_chunk_offsets(
    std::vector<uint8_t>& data,
    size_t moov_start,
    size_t threshold,
    int64_t delta) noexcept
{
    binary_reader reader(data);
    if (!reader.try_seek(moov_start)) return false;
    
    uint32_t moov_size = 0;
    if (!reader.try_read_be32u(moov_size) || moov_size < 8 || moov_size > data.size() - moov_start)
    {
        return false;
    }
    
    const size_t moov_end = moov_start + static_cast<size_t>(moov_size);
    size_t position = moov_start + 8;
    
    while (position + 8 <= moov_end)
    {
        if (!reader.try_seek(position)) return false;
        uint32_t child_size = 0;
        if (!reader.try_read_be32u(child_size) || child_size < 8 || child_size > moov_end - position)
        {
            return false;
        }
        
        if (is_type(data, position, "trak"))
        {
            if (!shift_trak_chunk_offsets(
                data, position, position + static_cast<size_t>(child_size),
                threshold, delta))
            {
                return false;
            }
        }
        position += static_cast<size_t>(child_size);
    }

    return true;
}
