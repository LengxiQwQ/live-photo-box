#include "foundation/internal.h"
#include "containers/isobmff.h"

bool is_type(const std::vector<uint8_t>& data, size_t offset, const char* type) noexcept
{
    return offset <= data.size() && data.size() - offset >= 8
        && data[offset + 4] == static_cast<uint8_t>(type[0])
        && data[offset + 5] == static_cast<uint8_t>(type[1])
        && data[offset + 6] == static_cast<uint8_t>(type[2])
        && data[offset + 7] == static_cast<uint8_t>(type[3]);
}

size_t find_child_box(
    const std::vector<uint8_t>& data,
    size_t start,
    size_t end,
    const char* type) noexcept
{
    size_t position = start;
    while (position <= end && end - position >= 8 && data.size() - position >= 8)
    {
        const int32_t signed_size = read_be32(data.data() + position);
        if (signed_size < 8)
        {
            break;
        }
        const size_t size = static_cast<size_t>(signed_size);
        if (size > end - position || size > data.size() - position)
        {
            break;
        }
        if (is_type(data, position, type))
        {
            return position;
        }
        position += size;
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
    const size_t mdia = find_child_box(data, trak_start + 8, trak_end, "mdia");
    if (mdia == missing)
    {
        return;
    }
    const int32_t mdia_size = read_be32(data.data() + mdia);
    if (mdia_size < 8 || static_cast<size_t>(mdia_size) > data.size() - mdia)
    {
        return;
    }
    const size_t mdia_end = mdia + static_cast<size_t>(mdia_size);
    const size_t minf = find_child_box(data, mdia + 8, mdia_end, "minf");
    if (minf == missing)
    {
        return;
    }
    const int32_t minf_size = read_be32(data.data() + minf);
    if (minf_size < 8 || static_cast<size_t>(minf_size) > data.size() - minf)
    {
        return;
    }
    const size_t minf_end = minf + static_cast<size_t>(minf_size);
    const size_t stbl = find_child_box(data, minf + 8, minf_end, "stbl");
    if (stbl == missing)
    {
        return;
    }
    const int32_t stbl_size = read_be32(data.data() + stbl);
    if (stbl_size < 8 || static_cast<size_t>(stbl_size) > data.size() - stbl)
    {
        return;
    }
    const size_t stbl_end = stbl + static_cast<size_t>(stbl_size);

    const size_t stco = find_child_box(data, stbl + 8, stbl_end, "stco");
    if (stco != missing && stco + 16 <= stbl_end)
    {
        const int32_t count = read_be32(data.data() + stco + 12);
        for (int32_t index = 0; index < count; ++index)
        {
            const size_t field = stco + 16 + static_cast<size_t>(index) * 4;
            if (field + 4 > stbl_end)
            {
                break;
            }
            const int32_t offset = read_be32(data.data() + field);
            if (offset > 0 && static_cast<size_t>(offset) > threshold)
            {
                write_be32(data.data() + field,
                    offset - static_cast<int32_t>(removed_bytes));
            }
        }
    }

    const size_t co64 = find_child_box(data, stbl + 8, stbl_end, "co64");
    if (co64 != missing && co64 + 16 <= stbl_end)
    {
        const int32_t count = read_be32(data.data() + co64 + 12);
        for (int32_t index = 0; index < count; ++index)
        {
            const size_t field = co64 + 16 + static_cast<size_t>(index) * 8;
            if (field + 8 > stbl_end)
            {
                break;
            }
            const int64_t offset = read_be64(data.data() + field);
            if (offset > 0 && static_cast<uint64_t>(offset) > threshold)
            {
                write_be64(data.data() + field,
                    offset - static_cast<int64_t>(removed_bytes));
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
    if (moov_start > data.size() || data.size() - moov_start < 8)
    {
        return;
    }
    const int32_t signed_size = read_be32(data.data() + moov_start);
    if (signed_size < 8 || static_cast<size_t>(signed_size) > data.size() - moov_start)
    {
        return;
    }
    const size_t moov_end = moov_start + static_cast<size_t>(signed_size);
    size_t position = moov_start + 8;
    while (position + 8 <= moov_end)
    {
        const int32_t child_size = read_be32(data.data() + position);
        if (child_size < 8 || static_cast<size_t>(child_size) > moov_end - position)
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
