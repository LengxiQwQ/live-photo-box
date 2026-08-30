#include "foundation/internal.h"
#include "containers/isobmff.h"
#include "binary/binary_io.h"
#include <limits>

using namespace lpb;

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

    while (reader.position() + 8 <= end)
    {
        size_t position = reader.position();
        uint32_t size = 0;
        if (!reader.try_read_be32u(size) || size < 8) break;
        if (size > end - position) break;

        if (is_type(data, position, type))
        {
            return position;
        }
        
        if (!reader.try_seek(position + size)) break;
    }
    return std::numeric_limits<size_t>::max();
}

size_t find_top_level_box(const std::vector<uint8_t>& data, const char* type) noexcept
{
    return find_child_box(data, 0, data.size(), type);
}

void adjust_trak_chunk_offsets(
    std::vector<uint8_t>& data,
    size_t trak_start,
    size_t trak_end,
    size_t threshold,
    size_t removed_bytes) noexcept
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
    if (mdia == missing) return;
    const size_t mdia_end = get_box_end(mdia, data.size());
    if (mdia_end == missing) return;

    const size_t minf = find_child_box(data, mdia + 8, mdia_end, "minf");
    if (minf == missing) return;
    const size_t minf_end = get_box_end(minf, data.size());
    if (minf_end == missing) return;

    const size_t stbl = find_child_box(data, minf + 8, minf_end, "stbl");
    if (stbl == missing) return;
    const size_t stbl_end = get_box_end(stbl, data.size());
    if (stbl_end == missing) return;

    const size_t stco = find_child_box(data, stbl + 8, stbl_end, "stco");
    if (stco != missing && stco + 16 <= stbl_end)
    {
        binary_reader reader(data);
        if (reader.try_seek(stco + 12)) {
            uint32_t count = 0;
            if (reader.try_read_be32u(count)) {
                binary_writer writer(data);
                for (uint32_t index = 0; index < count; ++index)
                {
                    size_t field = stco + 16 + static_cast<size_t>(index) * 4;
                    if (field + 4 > stbl_end) break;
                    
                    uint32_t offset = 0;
                    reader.try_seek(field);
                    reader.try_read_be32u(offset);
                    
                    if (offset > 0 && static_cast<size_t>(offset) > threshold)
                    {
                        writer.try_seek(field);
                        writer.try_write_be32u(offset - static_cast<uint32_t>(removed_bytes));
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
            if (reader.try_read_be32u(count)) {
                binary_writer writer(data);
                for (uint32_t index = 0; index < count; ++index)
                {
                    size_t field = co64 + 16 + static_cast<size_t>(index) * 8;
                    if (field + 8 > stbl_end) break;
                    
                    int64_t offset = 0;
                    reader.try_seek(field);
                    reader.try_read_be64(offset);
                    
                    if (offset > 0 && static_cast<uint64_t>(offset) > threshold)
                    {
                        writer.try_seek(field);
                        writer.try_write_be64(offset - static_cast<int64_t>(removed_bytes));
                    }
                }
            }
        }
    }
}

void adjust_chunk_offsets(
    std::vector<uint8_t>& data,
    size_t moov_start,
    size_t threshold,
    size_t removed_bytes) noexcept
{
    binary_reader reader(data);
    if (!reader.try_seek(moov_start)) return;
    
    uint32_t moov_size = 0;
    if (!reader.try_read_be32u(moov_size) || moov_size < 8 || moov_size > data.size() - moov_start)
    {
        return;
    }
    
    const size_t moov_end = moov_start + static_cast<size_t>(moov_size);
    size_t position = moov_start + 8;
    
    while (position + 8 <= moov_end)
    {
        if (!reader.try_seek(position)) break;
        uint32_t child_size = 0;
        if (!reader.try_read_be32u(child_size) || child_size < 8 || child_size > moov_end - position)
        {
            break;
        }
        
        if (is_type(data, position, "trak"))
        {
            adjust_trak_chunk_offsets(
                data, position, position + static_cast<size_t>(child_size),
                threshold, removed_bytes);
        }
        position += static_cast<size_t>(child_size);
    }
}
