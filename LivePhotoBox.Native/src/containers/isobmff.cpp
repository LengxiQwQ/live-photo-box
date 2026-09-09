#include "foundation/internal.h"
#include "containers/isobmff.h"
#include "binary/binary_io.h"
#include <algorithm>
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

namespace {
struct media_payload_range { size_t start{}; size_t end{}; };

bool type_is(const uint8_t* data, size_t position, const char type[4]) noexcept {
    return data != nullptr && std::memcmp(data + position + 4, type, 4) == 0;
}
bool direct_child(const uint8_t* data, size_t start, size_t end, const char type[4],
    isobmff_box_header& result, size_t& result_start) noexcept {
    size_t position = start;
    while (position < end) {
        isobmff_box_header box{};
        if (!try_read_box_header(data, position, end, box)) return false;
        if (type_is(data, position, type)) {
            if (result.size != 0) return false;
            result = box; result_start = position;
        }
        position += box.size;
    }
    return position == end;
}
bool read_full_box_count(const uint8_t* data, size_t start, size_t end,
    uint32_t& count, size_t& entries) noexcept {
    isobmff_box_header box{};
    if (!try_read_box_header(data, start, end, box) || start + box.size != end || box.size < box.header_size + 8) return false;
    const size_t body = start + box.header_size;
    count = read_be32u(data + body + 4);
    entries = body + 8;
    return true;
}

bool validate_video_table(const uint8_t* data, size_t stbl_start, size_t stbl_end,
    const std::vector<media_payload_range>& mdats, size_t origin) noexcept {
    isobmff_box_header stsd{}, stts{}, stsc{}, stsz{}, stz2{}, stco{}, co64{};
    size_t stsd_s=0, stts_s=0, stsc_s=0, stsz_s=0, stz2_s=0, stco_s=0, co64_s=0;
    if (!direct_child(data, stbl_start, stbl_end, "stsd", stsd, stsd_s) || stsd.size == 0 ||
        !direct_child(data, stbl_start, stbl_end, "stts", stts, stts_s) || stts.size == 0 ||
        !direct_child(data, stbl_start, stbl_end, "stsc", stsc, stsc_s) || stsc.size == 0 ||
        !direct_child(data, stbl_start, stbl_end, "stsz", stsz, stsz_s) ||
        !direct_child(data, stbl_start, stbl_end, "stz2", stz2, stz2_s) ||
        !direct_child(data, stbl_start, stbl_end, "stco", stco, stco_s) ||
        !direct_child(data, stbl_start, stbl_end, "co64", co64, co64_s)) return false;
    if (stsz.size == 0 && stz2.size == 0) return false;
    if (stco.size != 0 && co64.size != 0) return false;
    // The current portable validator intentionally does not implement the
    // compact stz2 table.  Reject it explicitly instead of treating its
    // bytes as stsz/stts.
    if (stz2.size != 0) return false;
    isobmff_box_header ctts{};
    size_t ctts_s = 0;
    if (!direct_child(data, stbl_start, stbl_end, "ctts", ctts, ctts_s)) return false;

    uint32_t description_count = 0;
    size_t entries = 0;
    if (!read_full_box_count(data, stsd_s, stsd_s + stsd.size, description_count, entries) ||
        description_count == 0) return false;
    size_t p = entries;
    for (uint32_t i = 0; i < description_count; ++i) {
        isobmff_box_header entry{};
        if (!try_read_box_header(data, p, stsd_s + stsd.size, entry)) return false;
        p += entry.size;
    }
    if (p != stsd_s + stsd.size) return false;

    uint32_t sample_count = 0;
    uint32_t fixed_sample_size = 0;
    std::vector<uint32_t> sample_sizes;
    {
        const size_t sample_box_start = stsz_s;
        const auto& sample_box = stsz;
        const size_t body = sample_box_start + sample_box.header_size;
        const size_t sample_box_end = sample_box_start + sample_box.size;
        if (body > sample_box_end || sample_box_end - body < 12) return false;
        fixed_sample_size = read_be32u(data + body + 4);
        sample_count = read_be32u(data + body + 8);
        if (sample_count == 0 || sample_count > 10'000'000u) return false;
        if (fixed_sample_size == 0) {
            const size_t table_start = body + 12;
            if (sample_count > (sample_box_end - table_start) / 4 ||
                table_start + static_cast<size_t>(sample_count) * 4 != sample_box_end) return false;
            sample_sizes.reserve(sample_count);
            for (uint32_t i = 0; i < sample_count; ++i) {
                sample_sizes.push_back(read_be32u(data + table_start + static_cast<size_t>(i) * 4));
            }
        } else if (body + 12 != sample_box_end) {
            return false;
        }
    }

    uint32_t timing_count = 0;
    if (!read_full_box_count(data, stts_s, stts_s + stts.size, timing_count, entries) ||
        timing_count == 0 || entries + static_cast<size_t>(timing_count) * 8 != stts_s + stts.size) return false;
    uint64_t timing_sample_count = 0;
    for (uint32_t i = 0; i < timing_count; ++i) {
        const uint32_t count = read_be32u(data + entries + static_cast<size_t>(i) * 8);
        if (count == 0) return false;
        if (timing_sample_count > std::numeric_limits<uint64_t>::max() - count) return false;
        timing_sample_count += count;
    }
    if (timing_sample_count != sample_count) return false;

    // Composition offsets are optional, but when present they must describe
    // exactly the same sample sequence as stts/stsz.  Apple MOV files and
    // camera MP4s commonly use ctts for B-frame presentation order.
    if (ctts.size != 0) {
        const size_t body = ctts_s + ctts.header_size;
        const size_t box_end = ctts_s + ctts.size;
        if (body > box_end || box_end - body < 8 || data[body] > 1) return false;
        const uint32_t count = read_be32u(data + body + 4);
        const size_t entries_start = body + 8;
        if (count > (box_end - entries_start) / 8 ||
            entries_start + static_cast<size_t>(count) * 8 != box_end || count == 0) return false;
        uint64_t composition_sample_count = 0;
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t entry_count = read_be32u(data + entries_start + static_cast<size_t>(i) * 8);
            if (entry_count == 0 ||
                composition_sample_count > std::numeric_limits<uint64_t>::max() - entry_count) return false;
            composition_sample_count += entry_count;
        }
        if (composition_sample_count != sample_count) return false;
    }

    uint32_t stsc_count = 0;
    if (!read_full_box_count(data, stsc_s, stsc_s + stsc.size, stsc_count, entries) ||
        stsc_count == 0 || entries + static_cast<size_t>(stsc_count) * 12 != stsc_s + stsc.size) return false;
    struct stsc_entry { uint32_t first_chunk; uint32_t samples_per_chunk; uint32_t description_index; };
    std::vector<stsc_entry> stsc_entries;
    stsc_entries.reserve(stsc_count);
    for (uint32_t i = 0; i < stsc_count; ++i) {
        const size_t entry = entries + static_cast<size_t>(i) * 12;
        const stsc_entry current{
            read_be32u(data + entry), read_be32u(data + entry + 4), read_be32u(data + entry + 8) };
        if (current.first_chunk == 0 || current.samples_per_chunk == 0 ||
            current.description_index == 0 || current.description_index > description_count ||
            (!stsc_entries.empty() && current.first_chunk <= stsc_entries.back().first_chunk)) return false;
        stsc_entries.push_back(current);
    }

    uint32_t chunk_count = 0;
    size_t offset_entries = 0;
    const size_t offset_width = stco.size ? 4 : 8;
    const size_t offset_box_start = stco.size ? stco_s : co64_s;
    const isobmff_box_header& offset_box = stco.size ? stco : co64;
    if (!read_full_box_count(data, offset_box_start, offset_box_start + offset_box.size,
            chunk_count, offset_entries) || chunk_count == 0 ||
        offset_entries + static_cast<size_t>(chunk_count) * offset_width != offset_box_start + offset_box.size) return false;

    if (chunk_count == 0 || chunk_count > 10'000'000u) return false;

    // Expand the compact stsc run table into one sample count per physical
    // chunk.  This is bounded by the validated stco/co64 entry count and lets
    // us prove both total sample coverage and each chunk's byte extent.
    std::vector<uint64_t> chunk_sample_counts(chunk_count, 0);
    uint64_t mapped_sample_count = 0;
    for (size_t i = 0; i < stsc_entries.size(); ++i) {
        const uint64_t first = stsc_entries[i].first_chunk;
        const uint64_t last = i + 1 < stsc_entries.size()
            ? static_cast<uint64_t>(stsc_entries[i + 1].first_chunk) - 1
            : chunk_count;
        if (first > chunk_count || last < first || last > chunk_count) return false;
        const uint64_t run_chunks = last - first + 1;
        if (run_chunks > std::numeric_limits<uint64_t>::max() /
                stsc_entries[i].samples_per_chunk) return false;
        const uint64_t run_samples = run_chunks * stsc_entries[i].samples_per_chunk;
        if (mapped_sample_count > std::numeric_limits<uint64_t>::max() - run_samples) return false;
        mapped_sample_count += run_samples;
        for (uint64_t chunk = first; chunk <= last; ++chunk) {
            chunk_sample_counts[static_cast<size_t>(chunk - 1)] = stsc_entries[i].samples_per_chunk;
        }
    }
    if (mapped_sample_count != sample_count) return false;

    // stco/co64 offsets are absolute in a standalone file, while an
    // embedded MP4 may retain offsets relative to the embedded range.  The
    // choice is a property of the whole sample table, never of an individual
    // chunk.  Trying both modes also prevents a relative first chunk from
    // being rebased while a later value happens to look like a host-file
    // offset (the source of the vivo X300 regression).
    auto validate_offset_mode = [&](uint64_t bias, std::vector<media_payload_range>& chunks) noexcept {
        chunks.clear();
        chunks.reserve(chunk_count);
        uint64_t sample_index = 0;
        for (uint32_t chunk = 1; chunk <= chunk_count; ++chunk) {
            const size_t field = offset_entries + static_cast<size_t>(chunk - 1) * offset_width;
            const uint64_t raw = offset_width == 4 ? read_be32u(data + field) : read_be64(data + field);
            if (raw > std::numeric_limits<uint64_t>::max() - bias) return false;
            const uint64_t absolute = raw + bias;
            auto containing = std::find_if(mdats.begin(), mdats.end(), [&](const media_payload_range& range) {
                return absolute >= range.start && absolute < range.end;
            });
            if (containing == mdats.end() || absolute > SIZE_MAX) return false;

            const uint64_t samples_in_chunk = chunk_sample_counts[static_cast<size_t>(chunk - 1)];
            if (samples_in_chunk == 0 || samples_in_chunk > sample_count ||
                sample_index > sample_count - samples_in_chunk) return false;
            uint64_t chunk_bytes = 0;
            for (uint64_t i = 0; i < samples_in_chunk; ++i) {
                const uint64_t size = fixed_sample_size != 0
                    ? fixed_sample_size : sample_sizes[static_cast<size_t>(sample_index + i)];
                if (chunk_bytes > std::numeric_limits<uint64_t>::max() - size) return false;
                chunk_bytes += size;
            }
            if (chunk_bytes == 0 || absolute > std::numeric_limits<uint64_t>::max() - chunk_bytes) return false;
            const uint64_t chunk_end = absolute + chunk_bytes;
            if (chunk_end > containing->end || chunk_end > SIZE_MAX) return false;
            chunks.push_back({ static_cast<size_t>(absolute), static_cast<size_t>(chunk_end) });
            sample_index += samples_in_chunk;
        }
        if (sample_index != sample_count) return false;
        std::sort(chunks.begin(), chunks.end(), [](const media_payload_range& left, const media_payload_range& right) {
            return left.start < right.start;
        });
        for (size_t i = 1; i < chunks.size(); ++i) {
            if (chunks[i - 1].end > chunks[i].start) return false;
        }
        return true;
    };

    std::vector<media_payload_range> absolute_chunks;
    const bool absolute_valid = validate_offset_mode(0, absolute_chunks);
    std::vector<media_payload_range> rebased_chunks;
    const bool rebased_valid = origin != 0 && validate_offset_mode(origin, rebased_chunks);
    if (!absolute_valid && !rebased_valid) return false;
    // If both coordinate systems describe the table, the bytes do not tell us
    // which owner the offsets refer to.  Treat that as ambiguous rather than
    // silently selecting one and risking an incorrect extraction.
    return absolute_valid != rebased_valid;
}
bool validate_media_range(const uint8_t* data, size_t data_size, uint64_t offset, uint64_t length, bool require_ftyp) noexcept {
    if (!data || offset > data_size || length < 8 || length > data_size - static_cast<size_t>(offset)) return false;
    const size_t start=static_cast<size_t>(offset), end=start+static_cast<size_t>(length);
    size_t pos=start, moov_start=0, moov_end=0, moov_header=0;
    bool ftyp=false, mdat=false, moov=false; std::vector<media_payload_range> mdats;
    while(pos<end) {
        isobmff_box_header box{};
        if(!try_read_box_header(data,pos,end,box)) return false;
        if(type_is(data,pos,"ftyp")) {
            if (ftyp || (require_ftyp && pos != start) || box.size < box.header_size + 8) return false;
            ftyp=true;
        }
        if(type_is(data,pos,"mdat")){
            mdat=true;
            mdats.push_back({pos+box.header_size,pos+box.size});
        }
        if(type_is(data,pos,"moov")){
            if(moov)return false;
            moov=true; moov_start=pos; moov_end=pos+box.size; moov_header=box.header_size;
        }
        pos+=box.size;
    }
    if(pos!=end || !mdat || !moov || (require_ftyp && !ftyp)) return false;
    bool video=false; pos=moov_start+moov_header;
    while(pos<moov_end) {
        isobmff_box_header trak{};
        if(!try_read_box_header(data,pos,moov_end,trak)) return false;
        if(type_is(data,pos,"trak")){
            const size_t trak_end=pos+trak.size;
            isobmff_box_header mdia{},hdlr{},minf{},stbl{};
            size_t mdia_s=0,hdlr_s=0,minf_s=0,stbl_s=0;
            if(!direct_child(data,pos+trak.header_size,trak_end,"mdia",mdia,mdia_s)||mdia.size==0)return false;
            const size_t mdia_end=mdia_s+mdia.size;
            if(!direct_child(data,mdia_s+mdia.header_size,mdia_end,"hdlr",hdlr,hdlr_s)||
                hdlr.size<hdlr.header_size+12||
                !direct_child(data,mdia_s+mdia.header_size,mdia_end,"minf",minf,minf_s)||minf.size==0)return false;
            const size_t handler=hdlr_s+hdlr.header_size+8;
            if(std::memcmp(data+handler,"vide",4)==0){
                const size_t minf_end=minf_s+minf.size;
                if(!direct_child(data,minf_s+minf.header_size,minf_end,"stbl",stbl,stbl_s)||stbl.size==0||
                    !validate_video_table(data,stbl_s+stbl.header_size,stbl_s+stbl.size,mdats,start))return false;
                video=true;
            }
        }
        pos+=trak.size;
    }
    return pos==moov_end && video;
}
}

bool is_valid_isobmff_media_range(const uint8_t* data,size_t data_size,uint64_t offset,uint64_t length) noexcept { return validate_media_range(data,data_size,offset,length,true); }
bool is_valid_mov_media_range(const uint8_t* data,size_t data_size,uint64_t offset,uint64_t length) noexcept { return validate_media_range(data,data_size,offset,length,false); }

bool is_type(std::span<const uint8_t> data, size_t offset, const char* type) noexcept
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
    std::span<const uint8_t> data,
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

size_t find_top_level_box(std::span<const uint8_t> data, const char* type) noexcept
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
        if (start > end_limit) return missing;
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
    if (stco != missing)
    {
        if (stco > stbl_end || stbl_end - stco < 16) return false;
        binary_reader reader(data);
        if (!reader.try_seek(stco + 12)) return false;
        uint32_t count = 0;
        if (!reader.try_read_be32u(count) || count > (stbl_end - stco - 16) / 4) return false;
        binary_writer writer(data);
        for (uint32_t index = 0; index < count; ++index)
        {
            size_t field = stco + 16 + static_cast<size_t>(index) * 4;
            if (field > stbl_end - 4) return false;

            uint32_t offset = 0;
            if (!reader.try_seek(field) || !reader.try_read_be32u(offset)) return false;

            if (offset > 0 && static_cast<size_t>(offset) > threshold)
            {
                if ((delta > 0 && offset > std::numeric_limits<int64_t>::max() - delta) ||
                    (delta < 0 && offset < std::numeric_limits<int64_t>::min() - delta)) return false;
                int64_t shifted = static_cast<int64_t>(offset) + delta;
                if (shifted <= 0 || shifted > static_cast<int64_t>(std::numeric_limits<uint32_t>::max())) {
                    return false; // underflow / overflow
                }
                if (!writer.try_seek(field) || !writer.try_write_be32u(static_cast<uint32_t>(shifted))) return false;
            }
        }
    }

    const size_t co64 = find_child_box(data, stbl + 8, stbl_end, "co64");
    if (co64 != missing)
    {
        if (co64 > stbl_end || stbl_end - co64 < 16) return false;
        binary_reader reader(data);
        if (!reader.try_seek(co64 + 12)) return false;
        uint32_t count = 0;
        if (!reader.try_read_be32u(count) || count > (stbl_end - co64 - 16) / 8) return false;
        binary_writer writer(data);
        for (uint32_t index = 0; index < count; ++index)
        {
            size_t field = co64 + 16 + static_cast<size_t>(index) * 8;
            if (field > stbl_end - 8) return false;

            int64_t offset = 0;
            if (!reader.try_seek(field) || !reader.try_read_be64(offset)) return false;

            if (offset > 0 && static_cast<uint64_t>(offset) > threshold)
            {
                if ((delta > 0 && offset > std::numeric_limits<int64_t>::max() - delta) ||
                    (delta < 0 && offset < std::numeric_limits<int64_t>::min() - delta)) return false;
                int64_t shifted = offset + delta;
                if (shifted <= 0) {
                    return false; // underflow
                }
                if (!writer.try_seek(field) || !writer.try_write_be64(shifted)) return false;
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
    
    while (position < moov_end)
    {
        if (moov_end - position < 8) return false;
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

    return position == moov_end;
}
