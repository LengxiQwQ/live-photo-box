#include "containers/isobmff.h"
#include "containers/aac_config.h"
#include "containers/pcm_config.h"
#include "containers/isobmff_metadata_keys.h"
#include "containers/isobmff_track_graph.h"
#include "containers/isobmff_video_profile.h"
#include "binary/binary_io.h"
#include <algorithm>
#include <cstring>
#include <limits>
#include <set>
#include <utility>

using namespace lpb;

std::vector<top_level_box> scan_top_level_boxes(
    const random_access_reader& source) noexcept
{
    std::vector<top_level_box> boxes;
    try {
        uint64_t position = 0;
        while (source.length - position >= 8) {
            uint8_t header[16]{};
            if (!source.read_exact(position, std::span<uint8_t>(header, 8))) break;
            uint64_t size = read_be32u(header);
            uint32_t header_size = 8;
            if (size == 1) {
                if (source.length - position < 16 ||
                    !source.read_exact(position + 8, std::span<uint8_t>(header + 8, 8))) break;
                size = static_cast<uint64_t>(read_be64(header + 8));
                header_size = 16;
            } else if (size == 0) {
                size = source.length - position;
            }
            if (size < header_size || size > source.length - position) break;
            top_level_box box{position, size, {}, header_size};
            std::memcpy(box.type, header + 4, 4);
            boxes.push_back(box);
            position += size;
        }
    } catch (...) {
        boxes.clear();
    }
    return boxes;
}

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

bool parse_sample_table_layout(std::span<const uint8_t> data, size_t stbl_start, size_t stbl_end,
    std::span<const isobmff_media_data_range> mdats, uint64_t origin,
    isobmff_sample_table_layout& output) noexcept {
    output = {};
    try {
    if (stbl_start > stbl_end || stbl_end > data.size()) return false;
    isobmff_box_header stsd{}, stts{}, stsc{}, stsz{}, stz2{}, stco{}, co64{};
    size_t stsd_s=0, stts_s=0, stsc_s=0, stsz_s=0, stz2_s=0, stco_s=0, co64_s=0;
    if (!direct_child(data.data(), stbl_start, stbl_end, "stsd", stsd, stsd_s) || stsd.size == 0 ||
        !direct_child(data.data(), stbl_start, stbl_end, "stts", stts, stts_s) || stts.size == 0 ||
        !direct_child(data.data(), stbl_start, stbl_end, "stsc", stsc, stsc_s) || stsc.size == 0 ||
        !direct_child(data.data(), stbl_start, stbl_end, "stsz", stsz, stsz_s) ||
        !direct_child(data.data(), stbl_start, stbl_end, "stz2", stz2, stz2_s) ||
        !direct_child(data.data(), stbl_start, stbl_end, "stco", stco, stco_s) ||
        !direct_child(data.data(), stbl_start, stbl_end, "co64", co64, co64_s)) return false;
    if (stsz.size == 0 && stz2.size == 0) return false;
    if (stco.size != 0 && co64.size != 0) return false;
    // The current portable validator intentionally does not implement the
    // compact stz2 table.  Reject it explicitly instead of treating its
    // bytes as stsz/stts.
    if (stz2.size != 0) return false;
    isobmff_box_header ctts{};
    size_t ctts_s = 0;
    if (!direct_child(data.data(), stbl_start, stbl_end, "ctts", ctts, ctts_s)) return false;

    uint32_t description_count = 0;
    size_t entries = 0;
    if (!read_full_box_count(data.data(), stsd_s, stsd_s + stsd.size, description_count, entries) ||
        description_count == 0) return false;
    size_t p = entries;
    for (uint32_t i = 0; i < description_count; ++i) {
        isobmff_box_header entry{};
        if (!try_read_box_header(data.data(), p, stsd_s + stsd.size, entry)) return false;
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
        fixed_sample_size = read_be32u(data.data() + body + 4);
        sample_count = read_be32u(data.data() + body + 8);
        if (sample_count == 0 || sample_count > 10'000'000u) return false;
        if (fixed_sample_size == 0) {
            const size_t table_start = body + 12;
            if (sample_count > (sample_box_end - table_start) / 4 ||
                table_start + static_cast<size_t>(sample_count) * 4 != sample_box_end) return false;
            sample_sizes.reserve(sample_count);
            for (uint32_t i = 0; i < sample_count; ++i) {
                sample_sizes.push_back(read_be32u(data.data() + table_start + static_cast<size_t>(i) * 4));
            }
        } else if (body + 12 != sample_box_end) {
            return false;
        }
    }

    uint32_t timing_count = 0;
    if (!read_full_box_count(data.data(), stts_s, stts_s + stts.size, timing_count, entries) ||
        timing_count == 0 || entries + static_cast<size_t>(timing_count) * 8 != stts_s + stts.size) return false;
    uint64_t timing_sample_count = 0;
    for (uint32_t i = 0; i < timing_count; ++i) {
        const uint32_t count = read_be32u(data.data() + entries + static_cast<size_t>(i) * 8);
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
        const uint32_t count = read_be32u(data.data() + body + 4);
        const size_t entries_start = body + 8;
        if (count > (box_end - entries_start) / 8 ||
            entries_start + static_cast<size_t>(count) * 8 != box_end || count == 0) return false;
        uint64_t composition_sample_count = 0;
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t entry_count = read_be32u(data.data() + entries_start + static_cast<size_t>(i) * 8);
            if (entry_count == 0 ||
                composition_sample_count > std::numeric_limits<uint64_t>::max() - entry_count) return false;
            composition_sample_count += entry_count;
        }
        if (composition_sample_count != sample_count) return false;
    }

    uint32_t stsc_count = 0;
    if (!read_full_box_count(data.data(), stsc_s, stsc_s + stsc.size, stsc_count, entries) ||
        stsc_count == 0 || entries + static_cast<size_t>(stsc_count) * 12 != stsc_s + stsc.size) return false;
    struct stsc_entry { uint32_t first_chunk; uint32_t samples_per_chunk; uint32_t description_index; };
    std::vector<stsc_entry> stsc_entries;
    stsc_entries.reserve(stsc_count);
    for (uint32_t i = 0; i < stsc_count; ++i) {
        const size_t entry = entries + static_cast<size_t>(i) * 12;
        const stsc_entry current{
            read_be32u(data.data() + entry), read_be32u(data.data() + entry + 4), read_be32u(data.data() + entry + 8) };
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
    if (!read_full_box_count(data.data(), offset_box_start, offset_box_start + offset_box.size,
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
    auto validate_offset_mode = [&](uint64_t bias, std::vector<isobmff_sample_chunk>& chunks) noexcept {
        chunks.clear();
        chunks.reserve(chunk_count);
        uint64_t sample_index = 0;
        for (uint32_t chunk = 1; chunk <= chunk_count; ++chunk) {
            const size_t field = offset_entries + static_cast<size_t>(chunk - 1) * offset_width;
            const uint64_t raw = offset_width == 4
                ? read_be32u(data.data() + field) : read_be64(data.data() + field);
            if (raw > std::numeric_limits<uint64_t>::max() - bias) return false;
            const uint64_t absolute = raw + bias;
            auto containing = std::find_if(mdats.begin(), mdats.end(), [&](const isobmff_media_data_range& range) {
                return absolute >= range.start && absolute < range.end;
            });
            if (containing == mdats.end() || absolute > SIZE_MAX || sample_index > UINT32_MAX) return false;

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
            chunks.push_back({ absolute, chunk_bytes, static_cast<uint32_t>(sample_index),
                static_cast<uint32_t>(samples_in_chunk) });
            sample_index += samples_in_chunk;
        }
        if (sample_index != sample_count) return false;
        std::vector<std::pair<uint64_t, uint64_t>> sorted_ranges;
        sorted_ranges.reserve(chunks.size());
        for (const auto& chunk : chunks) {
            sorted_ranges.emplace_back(chunk.offset, chunk.offset + chunk.size);
        }
        std::sort(sorted_ranges.begin(), sorted_ranges.end(), [](const auto& left, const auto& right) {
            return left.first < right.first;
        });
        for (size_t i = 1; i < sorted_ranges.size(); ++i) {
            if (sorted_ranges[i - 1].second > sorted_ranges[i].first) return false;
        }
        return true;
    };

    std::vector<isobmff_sample_chunk> absolute_chunks;
    const bool absolute_valid = validate_offset_mode(0, absolute_chunks);
    std::vector<isobmff_sample_chunk> rebased_chunks;
    const bool rebased_valid = origin != 0 && validate_offset_mode(origin, rebased_chunks);
    // If both coordinate systems describe the table, the bytes do not tell us
    // which owner the offsets refer to.  Treat that as ambiguous rather than
    // silently selecting one and risking an incorrect extraction.
    if (absolute_valid == rebased_valid) return false;
    output.chunk_offsets_entries_start = offset_entries;
    output.chunk_offset_width = offset_width;
    output.sample_count = sample_count;
    output.sample_description_count = description_count;
    output.chunks = absolute_valid ? std::move(absolute_chunks) : std::move(rebased_chunks);
    return true;
    } catch (...) {
        output = {};
        return false;
    }
}

bool validate_video_table(const uint8_t* data, size_t data_size,
    size_t stbl_start, size_t stbl_end,
    std::span<const isobmff_media_data_range> mdats, uint64_t origin) noexcept {
    isobmff_sample_table_layout layout{};
    return parse_sample_table_layout(std::span<const uint8_t>(data, data_size),
        stbl_start, stbl_end, mdats, origin, layout);
}
bool validate_media_range(const uint8_t* data, size_t data_size, uint64_t offset, uint64_t length, bool require_ftyp) noexcept {
    if (!data || offset > data_size || length < 8 || length > data_size - static_cast<size_t>(offset)) return false;
    const size_t start=static_cast<size_t>(offset), end=start+static_cast<size_t>(length);
    size_t pos=start, moov_start=0, moov_end=0, moov_header=0;
    bool ftyp=false, mdat=false, moov=false; std::vector<isobmff_media_data_range> mdats;
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
                    !validate_video_table(data,data_size,stbl_s+stbl.header_size,stbl_s+stbl.size,mdats,start))return false;
                video=true;
            }
        }
        pos+=trak.size;
    }
    return pos==moov_end && video;
}

bool checked_multiply_u64(uint64_t left, uint64_t right, uint64_t& result) noexcept {
    if (left != 0 && right > std::numeric_limits<uint64_t>::max() / left) return false;
    result = left * right;
    return true;
}

bool checked_add_u64(uint64_t left, uint64_t right, uint64_t& result) noexcept {
    if (right > std::numeric_limits<uint64_t>::max() - left) return false;
    result = left + right;
    return true;
}

uint64_t read_be64u_local(const uint8_t* data) noexcept {
    return (static_cast<uint64_t>(read_be32u(data)) << 32) |
        static_cast<uint64_t>(read_be32u(data + 4));
}

int64_t sign_extend_be32(uint32_t bits) noexcept {
    if (bits <= static_cast<uint32_t>(std::numeric_limits<int32_t>::max())) {
        return static_cast<int64_t>(bits);
    }
    return static_cast<int64_t>(bits) - 0x1'0000'0000LL;
}

int64_t sign_extend_be64(uint64_t bits) noexcept {
    if (bits <= static_cast<uint64_t>(std::numeric_limits<int64_t>::max())) {
        return static_cast<int64_t>(bits);
    }
    return -1 - static_cast<int64_t>(~bits);
}

bool ticks_to_100ns(uint64_t ticks, uint32_t timescale, int64_t& result) noexcept {
    constexpr uint64_t units_per_second = 10'000'000ULL;
    if (timescale == 0) return false;
    const uint64_t scale = timescale;
    const uint64_t whole_seconds = ticks / scale;
    const uint64_t remainder = ticks % scale;
    const uint64_t max_result = static_cast<uint64_t>(std::numeric_limits<int64_t>::max());
    if (whole_seconds > max_result / units_per_second) return false;
    uint64_t base = whole_seconds * units_per_second;
    uint64_t remainder_units = 0;
    if (!checked_multiply_u64(remainder, units_per_second, remainder_units)) return false;
    uint64_t fractional = remainder_units / scale;
    const uint64_t remainder_units_mod = remainder_units % scale;
    if (remainder_units_mod >= (scale + 1U) / 2U) ++fractional;
    if (fractional > max_result - base) return false;
    base += fractional;
    result = static_cast<int64_t>(base);
    return true;
}

bool normalized_time_to_100ns(uint64_t empty_duration, uint32_t movie_timescale,
    uint64_t media_delta, uint32_t media_timescale, int64_t& result) noexcept {
    constexpr uint64_t units_per_second = 10'000'000ULL;
    if (movie_timescale == 0 || media_timescale == 0) return false;
    const uint64_t movie_scale = movie_timescale;
    const uint64_t media_scale = media_timescale;
    const uint64_t movie_whole = empty_duration / movie_scale;
    const uint64_t media_whole = media_delta / media_scale;
    uint64_t whole_seconds = 0;
    if (!checked_add_u64(movie_whole, media_whole, whole_seconds)) return false;
    const uint64_t max_result = static_cast<uint64_t>(std::numeric_limits<int64_t>::max());
    if (whole_seconds > max_result / units_per_second) return false;
    uint64_t base = whole_seconds * units_per_second;

    uint64_t movie_remainder_units = 0;
    uint64_t media_remainder_units = 0;
    if (!checked_multiply_u64(empty_duration % movie_scale, units_per_second, movie_remainder_units) ||
        !checked_multiply_u64(media_delta % media_scale, units_per_second, media_remainder_units)) return false;
    uint64_t fractional_units = 0;
    const uint64_t movie_fraction = movie_remainder_units / movie_scale;
    const uint64_t media_fraction = media_remainder_units / media_scale;
    if (!checked_add_u64(movie_fraction, media_fraction, fractional_units)) return false;
    const uint64_t movie_remainder = movie_remainder_units % movie_scale;
    const uint64_t media_remainder = media_remainder_units % media_scale;

    uint64_t common_denominator = 0;
    uint64_t movie_remainder_scaled = 0;
    uint64_t media_remainder_scaled = 0;
    uint64_t combined_remainder = 0;
    if (!checked_multiply_u64(movie_scale, media_scale, common_denominator) ||
        !checked_multiply_u64(movie_remainder, media_scale, movie_remainder_scaled) ||
        !checked_multiply_u64(media_remainder, movie_scale, media_remainder_scaled) ||
        !checked_add_u64(movie_remainder_scaled, media_remainder_scaled, combined_remainder)) return false;
    fractional_units += combined_remainder / common_denominator;
    const uint64_t fractional_remainder = combined_remainder % common_denominator;
    if (fractional_remainder >= (common_denominator + 1U) / 2U) ++fractional_units;
    if (fractional_units > max_result - base) return false;
    base += fractional_units;
    result = static_cast<int64_t>(base);
    return true;
}

bool parse_media_header_timescale(std::span<const uint8_t> data, size_t start, size_t end,
    bool movie_header, uint32_t& timescale) noexcept {
    timescale = 0;
    isobmff_box_header box{};
    if (start > end || end > data.size() ||
        !try_read_box_header(data.data(), start, end, box) || start + box.size != end) return false;
    const size_t body = start + box.header_size;
    const size_t body_size = box.size - box.header_size;
    if (body_size < 4) return false;
    const uint8_t version = data[body];
    if (data[body + 1] != 0 || data[body + 2] != 0 || data[body + 3] != 0) return false;
    size_t field_offset = 0;
    size_t minimum_size = 0;
    if (version == 0) {
        field_offset = 12;
        minimum_size = movie_header ? 20 : 24;
    } else if (version == 1) {
        field_offset = 20;
        minimum_size = movie_header ? 32 : 36;
    } else {
        return false;
    }
    if (body_size < minimum_size || (!movie_header && body_size != minimum_size)) return false;
    timescale = read_be32u(data.data() + body + field_offset);
    return timescale != 0;
}

bool parse_full_box_table(std::span<const uint8_t> data, size_t start, size_t end,
    const char type[4], size_t entry_size, isobmff_box_header& box, uint8_t& version,
    uint32_t& entry_count, size_t& entries_start) noexcept {
    version = 0;
    entry_count = 0;
    entries_start = 0;
    if (start > end || end > data.size() ||
        !try_read_box_header(data.data(), start, end, box) || start + box.size != end ||
        !type_is(data.data(), start, type) || box.size < box.header_size + 8) return false;
    const size_t body = start + box.header_size;
    if (data[body + 1] != 0 || data[body + 2] != 0 || data[body + 3] != 0) return false;
    version = data[body];
    entry_count = read_be32u(data.data() + body + 4);
    entries_start = body + 8;
    const size_t remaining = start + box.size - entries_start;
    if (entry_size == 0 || entry_count > remaining / entry_size ||
        static_cast<size_t>(entry_count) * entry_size != remaining) return false;
    return true;
}

bool parse_edit_list(std::span<const uint8_t> data, size_t trak_start, size_t trak_end,
    uint32_t& movie_timescale, bool& has_edit, uint64_t& leading_empty_duration,
    int64_t& media_start, uint64_t& media_segment_duration,
    isobmff_reader_failure* failure = nullptr) noexcept {
    const auto unsupported = [&]() {
        if (failure) *failure = isobmff_reader_failure::unsupported;
        return false;
    };
    has_edit = false;
    leading_empty_duration = 0;
    media_start = 0;
    media_segment_duration = 0;
    isobmff_box_header edts{};
    size_t edts_start = 0;
    if (!direct_child(data.data(), trak_start, trak_end, "edts", edts, edts_start)) return false;
    if (edts.size == 0) return true;

    const size_t edts_children_start = edts_start + edts.header_size;
    const size_t edts_end = edts_start + edts.size;
    isobmff_box_header elst{};
    size_t elst_start = 0;
    if (!direct_child(data.data(), edts_children_start, edts_end, "elst", elst, elst_start) ||
        elst.size == 0 || elst_start != edts_children_start || elst_start + elst.size != edts_end) return false;

    const size_t body = elst_start + elst.header_size;
    const size_t body_size = elst.size - elst.header_size;
    if (body_size < 8 || data[body + 1] != 0 || data[body + 2] != 0 || data[body + 3] != 0) return false;
    const uint8_t version = data[body];
    const size_t entry_size = version == 0 ? 12U : version == 1 ? 20U : 0U;
    if (entry_size == 0) return unsupported();
    const uint32_t count = read_be32u(data.data() + body + 4);
    const size_t entries_start = body + 8;
    if (count == 0 || count > (elst_start + elst.size - entries_start) / entry_size ||
        static_cast<size_t>(count) * entry_size != elst_start + elst.size - entries_start) return false;
    if (count > 2) return unsupported();

    struct edit_entry { uint64_t segment_duration{}; int64_t media_time{}; uint16_t rate_integer{}; uint16_t rate_fraction{}; };
    edit_entry edits[2]{};
    for (uint32_t index = 0; index < count; ++index) {
        const size_t entry = entries_start + static_cast<size_t>(index) * entry_size;
        if (version == 0) {
            edits[index].segment_duration = read_be32u(data.data() + entry);
            edits[index].media_time = sign_extend_be32(read_be32u(data.data() + entry + 4));
            edits[index].rate_integer = read_be16u(data.data() + entry + 8);
            edits[index].rate_fraction = read_be16u(data.data() + entry + 10);
        } else {
            edits[index].segment_duration = read_be64u_local(data.data() + entry);
            edits[index].media_time = sign_extend_be64(read_be64u_local(data.data() + entry + 8));
            edits[index].rate_integer = read_be16u(data.data() + entry + 16);
            edits[index].rate_fraction = read_be16u(data.data() + entry + 18);
        }
        if (edits[index].segment_duration == 0 || edits[index].media_time < -1) return false;
        if (edits[index].rate_integer != 1 || edits[index].rate_fraction != 0) return unsupported();
    }
    if (count == 1) {
        if (edits[0].media_time < 0) return unsupported();
        media_start = edits[0].media_time;
        media_segment_duration = edits[0].segment_duration;
    } else {
        if (edits[0].media_time != -1 || edits[1].media_time < 0) return unsupported();
        leading_empty_duration = edits[0].segment_duration;
        media_start = edits[1].media_time;
        media_segment_duration = edits[1].segment_duration;
    }
    has_edit = true;
    return movie_timescale != 0;
}
}

namespace {
bool inspect_timeline_moov(std::span<const uint8_t> data,
    const isobmff_box_header& moov, size_t moov_start, size_t moov_end,
    std::span<const isobmff_media_data_range> mdats, uint64_t coordinate_origin,
    bool quicktime_brand, isobmff_video_presentation_timeline& output,
    isobmff_reader_failure* failure = nullptr) noexcept
{
    output = {};
    if (failure) *failure = isobmff_reader_failure::malformed;
    const auto unsupported = [&]() {
        if (failure) *failure = isobmff_reader_failure::unsupported;
        return false;
    };
    try {
        isobmff_box_header mvhd{};
        size_t mvhd_start = 0;
        if (!direct_child(data.data(), moov_start + moov.header_size, moov_end, "mvhd", mvhd, mvhd_start) ||
            mvhd.size == 0 || !parse_media_header_timescale(data, mvhd_start, mvhd_start + mvhd.size,
                true, output.movie_timescale)) return false;

        bool found_video = false;
        isobmff_video_presentation_timeline parsed{};
        parsed.movie_timescale = output.movie_timescale;
        size_t child_position = moov_start + moov.header_size;
        while (child_position < moov_end) {
            isobmff_box_header child{};
            if (!try_read_box_header(data.data(), child_position, moov_end, child)) return false;
            if (type_is(data.data(), child_position, "trak")) {
                const size_t trak_end = child_position + child.size;
                isobmff_box_header mdia{};
                size_t mdia_start = 0;
                if (!direct_child(data.data(), child_position + child.header_size, trak_end,
                        "mdia", mdia, mdia_start) || mdia.size == 0) return false;
                const size_t mdia_end = mdia_start + mdia.size;
                isobmff_box_header hdlr{};
                size_t hdlr_start = 0;
                if (!direct_child(data.data(), mdia_start + mdia.header_size, mdia_end,
                        "hdlr", hdlr, hdlr_start) || hdlr.size < hdlr.header_size + 12) return false;
                const size_t handler_type = hdlr_start + hdlr.header_size + 8;
                if (std::memcmp(data.data() + handler_type, "vide", 4) == 0) {
                    if (found_video) return unsupported();
                    found_video = true;

                    isobmff_box_header mdhd{};
                    size_t mdhd_start = 0;
                    isobmff_box_header minf{};
                    size_t minf_start = 0;
                    if (!direct_child(data.data(), mdia_start + mdia.header_size, mdia_end,
                            "mdhd", mdhd, mdhd_start) || mdhd.size == 0 ||
                        !parse_media_header_timescale(data, mdhd_start, mdhd_start + mdhd.size,
                            false, parsed.media_timescale) ||
                        !direct_child(data.data(), mdia_start + mdia.header_size, mdia_end,
                            "minf", minf, minf_start) || minf.size == 0) return false;
                    const size_t minf_end = minf_start + minf.size;
                    isobmff_box_header stbl{};
                    size_t stbl_start = 0;
                    if (!direct_child(data.data(), minf_start + minf.header_size, minf_end,
                            "stbl", stbl, stbl_start) || stbl.size == 0) return false;
                    isobmff_sample_table_layout sample_table{};
                    if (!parse_sample_table_layout(data, stbl_start + stbl.header_size,
                            stbl_start + stbl.size, mdats, coordinate_origin, sample_table)) return false;
                    parsed.sample_count = sample_table.sample_count;

                    isobmff_box_header stts{};
                    size_t stts_start = 0;
                    isobmff_box_header ctts{};
                    size_t ctts_start = 0;
                    if (!direct_child(data.data(), stbl_start + stbl.header_size,
                            stbl_start + stbl.size, "stts", stts, stts_start) || stts.size == 0 ||
                        !direct_child(data.data(), stbl_start + stbl.header_size,
                            stbl_start + stbl.size, "ctts", ctts, ctts_start)) return false;

                    uint8_t stts_version = 0;
                    uint32_t stts_count = 0;
                    size_t stts_entries = 0;
                    if (!parse_full_box_table(data, stts_start, stts_start + stts.size,
                            "stts", 8, stts, stts_version, stts_count, stts_entries) ||
                        stts_version != 0 || stts_count == 0) return false;
                    std::vector<uint32_t> decode_durations;
                    decode_durations.reserve(parsed.sample_count);
                    for (uint32_t run = 0; run < stts_count; ++run) {
                        const size_t entry = stts_entries + static_cast<size_t>(run) * 8;
                        const uint32_t count = read_be32u(data.data() + entry);
                        const uint32_t delta = read_be32u(data.data() + entry + 4);
                        if (count == 0 || delta == 0 || count > parsed.sample_count - decode_durations.size()) return false;
                        decode_durations.insert(decode_durations.end(), count, delta);
                    }
                    if (decode_durations.size() != parsed.sample_count) return false;

                    std::vector<int64_t> composition_offsets(parsed.sample_count, 0);
                    if (ctts.size != 0) {
                        uint8_t ctts_version = 0;
                        uint32_t ctts_count = 0;
                        size_t ctts_entries = 0;
                        if (!parse_full_box_table(data, ctts_start, ctts_start + ctts.size,
                                "ctts", 8, ctts, ctts_version, ctts_count, ctts_entries) ||
                            (ctts_version != 0 && ctts_version != 1) || ctts_count == 0) return false;
                        size_t sample = 0;
                        for (uint32_t run = 0; run < ctts_count; ++run) {
                            const size_t entry = ctts_entries + static_cast<size_t>(run) * 8;
                            const uint32_t count = read_be32u(data.data() + entry);
                            const uint32_t raw_offset = read_be32u(data.data() + entry + 4);
                            if (count == 0 || count > parsed.sample_count - sample) return false;
                            int64_t offset = static_cast<int64_t>(raw_offset);
                            // Apple QuickTime files permit negative composition deltas; their
                            // legacy version-0 tables store the two's-complement value without
                            // changing the version byte. ISO-BMFF version-0 tables stay unsigned.
                            if (ctts_version == 1 || quicktime_brand) offset = sign_extend_be32(raw_offset);
                            std::fill_n(composition_offsets.begin() + static_cast<std::ptrdiff_t>(sample), count, offset);
                            sample += count;
                        }
                        if (sample != parsed.sample_count) return false;
                    }

                    struct decode_entry {
                        uint32_t sample_index{};
                        int64_t decode_time{};
                        int64_t presentation_time{};
                        uint32_t decode_duration{};
                    };
                    std::vector<decode_entry> entries;
                    entries.reserve(parsed.sample_count);
                    uint64_t decode_time = 0;
                    for (uint32_t sample = 0; sample < parsed.sample_count; ++sample) {
                        if (decode_time > static_cast<uint64_t>(std::numeric_limits<int64_t>::max())) return false;
                        const int64_t signed_decode = static_cast<int64_t>(decode_time);
                        const int64_t offset = composition_offsets[sample];
                        if ((offset > 0 && signed_decode > std::numeric_limits<int64_t>::max() - offset) ||
                            (offset < 0 && signed_decode < std::numeric_limits<int64_t>::min() - offset)) return false;
                        entries.push_back({sample, signed_decode, signed_decode + offset, decode_durations[sample]});
                        if (!checked_add_u64(decode_time, decode_durations[sample], decode_time) ||
                            decode_time > static_cast<uint64_t>(std::numeric_limits<int64_t>::max())) return false;
                    }
                    std::sort(entries.begin(), entries.end(), [](const decode_entry& left, const decode_entry& right) {
                        if (left.presentation_time != right.presentation_time) {
                            return left.presentation_time < right.presentation_time;
                        }
                        return left.sample_index < right.sample_index;
                    });
                    for (size_t index = 1; index < entries.size(); ++index) {
                        if (entries[index - 1].presentation_time == entries[index].presentation_time) return false;
                    }

                    std::vector<uint64_t> presentation_durations(entries.size(), 0);
                    for (size_t index = 0; index + 1 < entries.size(); ++index) {
                        const uint64_t delta = static_cast<uint64_t>(entries[index + 1].presentation_time) -
                            static_cast<uint64_t>(entries[index].presentation_time);
                        if (delta == 0 || delta > static_cast<uint64_t>(std::numeric_limits<int64_t>::max())) return false;
                        presentation_durations[index] = delta;
                    }
                    presentation_durations.back() = entries.back().decode_duration;

                    bool has_edit = false;
                    uint64_t leading_empty_duration = 0;
                    int64_t media_start = 0;
                    uint64_t media_segment_duration = 0;
                    if (!parse_edit_list(data, child_position + child.header_size, trak_end,
                            parsed.movie_timescale, has_edit, leading_empty_duration,
                            media_start, media_segment_duration, failure)) return false;
                    parsed.media_edit_start_ticks = has_edit ? media_start : entries.front().presentation_time;
                    parsed.leading_empty_duration_ticks = leading_empty_duration;

                    uint64_t edit_end_numerator = 0;
                    if (has_edit && !checked_multiply_u64(media_segment_duration,
                            parsed.media_timescale, edit_end_numerator)) return false;
                    parsed.presentation_order.reserve(entries.size());
                    int64_t previous_visible_time = -1;
                    for (size_t index = 0; index < entries.size(); ++index) {
                        const auto& entry = entries[index];
                        const uint64_t duration = presentation_durations[index];
                        if (duration == 0 || duration > static_cast<uint64_t>(std::numeric_limits<int64_t>::max()) ||
                            entry.presentation_time > std::numeric_limits<int64_t>::max() - static_cast<int64_t>(duration)) return false;
                        const int64_t presentation_end = entry.presentation_time + static_cast<int64_t>(duration);
                        bool visible = !has_edit;
                        uint64_t elapsed_media_ticks = 0;
                        if (has_edit) {
                            if (entry.presentation_time < media_start) {
                                if (presentation_end > media_start) return unsupported();
                            } else {
                                elapsed_media_ticks = static_cast<uint64_t>(entry.presentation_time - media_start);
                                uint64_t start_numerator = 0;
                                if (!checked_multiply_u64(elapsed_media_ticks, parsed.movie_timescale,
                                        start_numerator)) return false;
                                visible = start_numerator < edit_end_numerator;
                                if (visible) {
                                    uint64_t end_delta = 0;
                                    uint64_t end_numerator = 0;
                                    if (!checked_add_u64(elapsed_media_ticks, duration, end_delta) ||
                                        !checked_multiply_u64(end_delta, parsed.movie_timescale, end_numerator) ||
                                        end_numerator > edit_end_numerator) return unsupported();
                                }
                            }
                        } else {
                            const uint64_t normalized_ticks = static_cast<uint64_t>(entry.presentation_time) -
                                static_cast<uint64_t>(entries.front().presentation_time);
                            elapsed_media_ticks = normalized_ticks;
                        }

                        isobmff_video_presentation_sample result{};
                        result.sample_index = entry.sample_index;
                        result.presentation_time_ticks = entry.presentation_time;
                        result.presentation_duration_ticks = duration;
                        result.visible = visible;
                        if (visible) {
                            const uint64_t normalized_ticks = has_edit
                                ? elapsed_media_ticks
                                : static_cast<uint64_t>(entry.presentation_time) -
                                    static_cast<uint64_t>(entries.front().presentation_time);
                            if (!normalized_time_to_100ns(leading_empty_duration, parsed.movie_timescale,
                                    normalized_ticks, parsed.media_timescale, result.normalized_time_100ns) ||
                                !ticks_to_100ns(duration, parsed.media_timescale, result.duration_100ns) ||
                                result.duration_100ns <= 0 || result.normalized_time_100ns <= previous_visible_time) return false;
                            previous_visible_time = result.normalized_time_100ns;
                            ++parsed.visible_sample_count;
                        }
                        parsed.presentation_order.push_back(result);
                    }
                }
            }
            child_position += child.size;
        }
        if (child_position != moov_end || !found_video || parsed.sample_count == 0 ||
            parsed.visible_sample_count == 0 || parsed.presentation_order.size() != parsed.sample_count) return false;
        output = std::move(parsed);
        if (failure) *failure = isobmff_reader_failure::none;
        return true;
    } catch (...) {
        if (failure) *failure = isobmff_reader_failure::unsupported;
        output = {};
        return false;
    }
}

}

bool inspect_isobmff_video_presentation_timeline(
    std::span<const uint8_t> data,
    isobmff_video_presentation_timeline& output,
    std::span<const isobmff_media_data_range> pinned_media_ranges) noexcept
{
    output = {};
    try {
        if (data.size() < 16) return false;
        std::vector<isobmff_media_data_range> mdats;
        size_t moov_start = 0;
        size_t moov_end = 0;
        isobmff_box_header moov{};
        size_t ftyp_count = 0;
        bool quicktime_brand = false;
        size_t position = 0;
        while (position < data.size()) {
            isobmff_box_header box{};
            if (!try_read_box_header(data.data(), position, data.size(), box)) return false;
            if (type_is(data.data(), position, "moov")) {
                if (moov.size != 0) return false;
                moov = box;
                moov_start = position;
                moov_end = position + box.size;
            } else if (type_is(data.data(), position, "mdat")) {
                mdats.push_back({position + box.header_size, position + box.size});
            } else if (type_is(data.data(), position, "ftyp")) {
                ++ftyp_count;
                const size_t payload = position + box.header_size;
                const size_t payload_size = box.size - box.header_size;
                if (ftyp_count != 1 || payload_size < 8 || (payload_size - 8) % 4 != 0) return false;
                if (std::memcmp(data.data() + payload, "qt  ", 4) == 0) quicktime_brand = true;
                for (size_t brand = payload + 8; brand < position + box.size; brand += 4) {
                    if (std::memcmp(data.data() + brand, "qt  ", 4) == 0) quicktime_brand = true;
                }
            }
            position += box.size;
        }
        if (position != data.size() || moov.size == 0 || mdats.empty()) return false;
        if(!pinned_media_ranges.empty()) {
            uint64_t previous=0;
            for(const auto& range:pinned_media_ranges) {
                if(range.start>=range.end||range.start<previous)return false;
                previous=range.end;
            }
            mdats.assign(pinned_media_ranges.begin(),pinned_media_ranges.end());
        }

        return inspect_timeline_moov(data, moov, moov_start, moov_end,
            mdats, 0, quicktime_brand, output);
    } catch (...) {
        output = {};
        return false;
    }
}

bool inspect_isobmff_still_image_time(std::span<const uint8_t> data,
    bool& present, int64_t& timestamp_us) noexcept {
    present = false; timestamp_us = 0;
    try {
        isobmff_box_header moov{}; size_t ms = 0;
        if (!direct_child(data.data(), 0, data.size(), "moov", moov, ms) || moov.size == 0) return false;
        isobmff_box_header mvhd{}; size_t vs = 0; uint32_t scale = 0;
        if (!direct_child(data.data(), ms + moov.header_size, ms + moov.size, "mvhd", mvhd, vs) ||
            mvhd.size == 0 || !parse_media_header_timescale(data, vs, vs + mvhd.size, true, scale)) return false;
        std::vector<isobmff_media_data_range> mdats;
        for (size_t p = 0; p < data.size();) {
            isobmff_box_header box{};
            if (!try_read_box_header(data.data(), p, data.size(), box)) return false;
            if (type_is(data.data(), p, "mdat")) mdats.push_back({p + box.header_size, p + box.size});
            p += box.size;
        }
        constexpr char key_name[] = "com.apple.quicktime.still-image-time";
        for (size_t p = ms + moov.header_size; p < ms + moov.size;) {
            isobmff_box_header track{};
            if (!try_read_box_header(data.data(), p, ms + moov.size, track)) return false;
            const size_t te = p + track.size;
            if (type_is(data.data(), p, "trak")) {
                isobmff_box_header mdia{}, hdlr{}, minf{}, stbl{}, stsd{};
                size_t a=0,b=0,c=0,d=0,e=0;
                if (!direct_child(data.data(), p+track.header_size, te, "mdia", mdia,a) || mdia.size==0 ||
                    !direct_child(data.data(), a+mdia.header_size,a+mdia.size,"hdlr",hdlr,b) || hdlr.size<hdlr.header_size+12) return false;
                if (std::memcmp(data.data()+b+hdlr.header_size+8,"meta",4)==0) {
                    if (!direct_child(data.data(),a+mdia.header_size,a+mdia.size,"minf",minf,c) || minf.size==0 ||
                        !direct_child(data.data(),c+minf.header_size,c+minf.size,"stbl",stbl,d) || stbl.size==0 ||
                        !direct_child(data.data(),d+stbl.header_size,d+stbl.size,"stsd",stsd,e) || stsd.size<stsd.header_size+8) return false;
                    bool owns_key=false;uint32_t owned_key_id=0;
                    const size_t desc=e+stsd.header_size;
                    if (read_be32u(data.data()+desc)!=0) return false;
                    uint32_t count=read_be32u(data.data()+desc+4); size_t ep=desc+8;
                    if (count!=1 || count>(e+stsd.size-ep)/8) return false;
                    for(uint32_t i=0;i<count;++i) {
                        isobmff_box_header entry{};
                        if(!try_read_box_header(data.data(),ep,e+stsd.size,entry))return false;
                        if(type_is(data.data(),ep,"mebx")) {
                            if(entry.size<entry.header_size+8)return false;
                            isobmff_box_header keys{}; size_t ks=0;
                            if(!direct_child(data.data(),ep+entry.header_size+8,ep+entry.size,"keys",keys,ks))return false;
                            std::set<uint32_t> key_ids;
                            if(keys.size) for(size_t kp=ks+keys.header_size;kp<ks+keys.size;) {
                                isobmff_box_header item{},keyd{}; size_t kd=0;
                                if(!try_read_box_header(data.data(),kp,ks+keys.size,item) ||
                                    !direct_child(data.data(),kp+item.header_size,kp+item.size,"keyd",keyd,kd))return false;
                                const uint32_t key_id=read_be32u(data.data()+kp+4);
                                if(key_id==0||!key_ids.insert(key_id).second)return false;
                                if(keyd.size==keyd.header_size+4+sizeof(key_name)-1 &&
                                    std::memcmp(data.data()+kd+keyd.header_size,"mdta",4)==0 &&
                                    std::memcmp(data.data()+kd+keyd.header_size+4,key_name,sizeof(key_name)-1)==0) {
                                    if(owns_key)return false; owns_key=true;
                                    owned_key_id=read_be32u(data.data()+kp+4);
                                    if(owned_key_id==0)return false;
                                }
                                kp+=item.size;
                            }
                        }
                        ep+=entry.size;
                    }
                    if(ep!=e+stsd.size)return false;
                    if(owns_key) {
                        if(present)return false;
                        isobmff_sample_table_layout table{};
                        if(!parse_sample_table_layout(data,d+stbl.header_size,d+stbl.size,mdats,0,table) || table.sample_count!=1)return false;
                        if(table.chunks.size()!=1)return false;
                        const auto& chunk=table.chunks.front();
                        if(chunk.offset>data.size()||chunk.size>data.size()-chunk.offset)return false;
                        bool sample_owns_key=false;
                        for(size_t sp=static_cast<size_t>(chunk.offset);sp<chunk.offset+chunk.size;) {
                            isobmff_box_header item{};
                            if(!try_read_box_header(data.data(),sp,static_cast<size_t>(chunk.offset+chunk.size),item))return false;
                            if(read_be32u(data.data()+sp+4)==owned_key_id) {
                                if(sample_owns_key||item.size!=item.header_size+1||data[sp+item.header_size]!=0xff)return false;
                                sample_owns_key=true;
                            }
                            sp+=item.size;
                        }
                        if(!sample_owns_key)return false;
                        bool edit=false; uint64_t empty=0,segment=0; int64_t start=0;
                        if(!parse_edit_list(data,p+track.header_size,te,scale,edit,empty,start,segment) || !edit ||scale==0 || start!=0)return false;
                        // Only the single sample presented at media time zero is
                        // supported here. A key in mdat is not timing authority
                        // when the edit skips it or ctts moves its presentation.
                        isobmff_box_header stts{},ctts{}; size_t ts=0,cs=0;
                        uint8_t version=0; uint32_t runs=0; size_t entries=0;
                        if(!direct_child(data.data(),d+stbl.header_size,d+stbl.size,"stts",stts,ts) || stts.size==0 ||
                            !parse_full_box_table(data,ts,ts+stts.size,"stts",8,stts,version,runs,entries) ||
                            version!=0 || runs!=1 || read_be32u(data.data()+entries)!=1 ||
                            read_be32u(data.data()+entries+4)==0)return false;
                        if(!direct_child(data.data(),d+stbl.header_size,d+stbl.size,"ctts",ctts,cs))return false;
                        if(ctts.size && (!parse_full_box_table(data,cs,cs+ctts.size,"ctts",8,ctts,version,runs,entries) ||
                            version>1 || runs!=1 || read_be32u(data.data()+entries)!=1 ||
                            read_be32u(data.data()+entries+4)!=0))return false;
                        const uint64_t whole=empty/scale,remainder=empty%scale;
                        if(whole>static_cast<uint64_t>(INT64_MAX)/1000000)return false;
                        const uint64_t fraction=(remainder*1000000+scale-1)/scale;
                        if(whole*1000000>static_cast<uint64_t>(INT64_MAX)-fraction)return false;
                        timestamp_us=static_cast<int64_t>(whole*1000000+fraction); present=true;
                    }
                }
            }
            p=te;
        }
        return true;
    } catch(...) {present=false;timestamp_us=0;return false;}
}

bool inspect_isobmff_sample_table(
    std::span<const uint8_t> data,
    size_t stbl_start,
    size_t stbl_end,
    std::span<const isobmff_media_data_range> mdats,
    uint64_t coordinate_origin,
    isobmff_sample_table_layout& output) noexcept
{
    return parse_sample_table_layout(data, stbl_start, stbl_end, mdats, coordinate_origin, output);
}

bool inspect_isobmff_strict_sample_table(std::span<const uint8_t> data,
    size_t ss,size_t end,std::span<const isobmff_media_data_range> mdats,
    uint64_t coordinate_origin,isobmff_sample_table_layout& output,uint64_t& bad) noexcept {
    output={};bad=ss;
    try {
        const auto fail=[&](size_t at){output={};bad=at;return false;};
        isobmff_box_header stbl{};
        if(ss>end||end>data.size()||!try_read_box_header(data.data(),ss,end,stbl)||
           ss+stbl.size!=end||!type_is(data.data(),ss,"stbl"))return false;
        auto& table=output;
        for (const char* table_type : {"stsd", "stts", "stsc", "stsz", "stco", "co64", "ctts"}) {
            isobmff_box_header full{}; size_t bs = 0;
            if (!direct_child(data.data(), ss + stbl.header_size, ss + stbl.size, table_type, full, bs))
                return fail(ss);
            if (!full.size) continue;
            if (full.size < full.header_size + 4) return fail(bs);
            const size_t fb = bs + full.header_size;
            if (data[fb + 1] || data[fb + 2] || data[fb + 3] ||
                data[fb] > (std::strcmp(table_type, "ctts") == 0 ? 1 : 0))
                return fail(bs);
            if (std::strcmp(table_type, "stts") == 0 && full.size >= full.header_size + 8) {
                const uint32_t count = read_be32u(data.data() + fb + 4);
                if (count > (bs + full.size - fb - 8) / 8) return fail(bs);
                for (uint32_t run = 0; run < count; ++run)
                    if (!read_be32u(data.data() + fb + 12 + static_cast<size_t>(run) * 8))
                        return fail(bs);
            }
            if (std::strcmp(table_type, "stsz") == 0 && full.size >= full.header_size + 12 &&
                read_be32u(data.data() + fb + 4) == 0) {
                const uint32_t count = read_be32u(data.data() + fb + 8);
                if (count > (bs + full.size - fb - 12) / 4) return fail(bs);
                for (uint32_t sample = 0; sample < count; ++sample)
                    if (!read_be32u(data.data() + fb + 12 + static_cast<size_t>(sample) * 4))
                        return fail(bs);
            }
        }
        if (!parse_sample_table_layout(data, ss + stbl.header_size, ss + stbl.size, mdats, coordinate_origin, table)) return fail(ss);
        // An optional sync-sample table is an owned sample reference set,
        // not opaque metadata. Missing stss means all samples are sync.
        isobmff_box_header sync{}; size_t sync_start = 0;
        if (!direct_child(data.data(), ss + stbl.header_size, ss + stbl.size,
            "stss", sync, sync_start)) return fail(ss);
        if (sync.size) {
            if (sync.size < sync.header_size + 8) return fail(sync_start);
            const size_t payload = sync_start + sync.header_size;
            const size_t entries_size = sync.size - sync.header_size - 8;
            const uint32_t count = read_be32u(data.data() + payload + 4);
            if (read_be32u(data.data() + payload) != 0 || entries_size % 4 ||
                count != entries_size / 4 || count > table.sample_count)
                return fail(sync_start);
            uint32_t previous = 0;
            for (uint32_t index = 0; index < count; ++index) {
                const uint32_t sample = read_be32u(data.data() + payload + 8 + static_cast<size_t>(index) * 4);
                if (sample <= previous || sample > table.sample_count)
                    return fail(payload + 8 + static_cast<size_t>(index) * 4);
                previous = sample;
            }
        }
        return true;
    }catch(...){output={};return false;}
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

bool inspect_isobmff_reader(const lpb::random_access_reader& reader, bool require_ftyp,
    isobmff_reader_observation& output, isobmff_reader_diagnostic& diagnostic) noexcept
{
    output = {}; diagnostic = {};
    uint64_t current_offset = 0;
    const auto fail = [&](isobmff_reader_failure reason, uint64_t offset) {
        diagnostic = {reason, offset}; output = {}; return false;
    };
    try {
        isobmff_reader_observation parsed{};
        uint64_t position = 0, moov_offset = 0;
        bool have_moov = false, have_ftyp = false;
        while (position < reader.length) {
            current_offset = position;
            if (reader.length - position < 8) return fail(isobmff_reader_failure::malformed, position);
            uint8_t header[16]{};
            if (!reader.read_exact(position, std::span<uint8_t>(header, 8))) return fail(isobmff_reader_failure::io, position);
            uint64_t size = read_be32u(header);
            uint32_t header_size = 8;
            if (size == 1) {
                if (reader.length - position < 16) return fail(isobmff_reader_failure::malformed, position);
                if (!reader.read_exact(position + 8, std::span<uint8_t>(header + 8, 8))) return fail(isobmff_reader_failure::io, position + 8);
                size = read_be64u_local(header + 8); header_size = 16;
            } else if (size == 0) size = reader.length - position;
            if (size < header_size || size > reader.length - position) return fail(isobmff_reader_failure::malformed, position);
            top_level_box box{position, size, {}, header_size};
            std::memcpy(box.type, header + 4, 4);
            parsed.top_level_boxes.push_back(box);
            const auto type = [&](const char* value) { return std::memcmp(box.type, value, 4) == 0; };
            if(type("uuid"))parsed.opaque_metadata=true;
            if (type("ftyp")) {
                if (have_ftyp || (require_ftyp && position != 0) || size - header_size < 8 ||
                    (size - header_size - 8) % 4 != 0) return fail(isobmff_reader_failure::malformed, position);
                have_ftyp = true;
                // Only brand bytes are read; no total-file or ftyp snapshot cap.
                uint8_t brands[4096]{};
                uint64_t cursor = position + header_size;
                while (cursor < position + size) {
                    const size_t count = static_cast<size_t>(std::min<uint64_t>(sizeof(brands), position + size - cursor));
                    if (!reader.read_exact(cursor, std::span<uint8_t>(brands, count))) return fail(isobmff_reader_failure::io, cursor);
                    for (size_t i = 0; i < count; i += 4) {
                        if (cursor + i != position + header_size + 4 && std::memcmp(brands + i, "qt  ", 4) == 0) parsed.quicktime = true;
                    }
                    cursor += count;
                }
            } else if (type("moov")) {
                if (have_moov) return fail(isobmff_reader_failure::malformed, position);
                have_moov = true; moov_offset = position;
                if (size > 64ULL * 1024 * 1024) return fail(isobmff_reader_failure::unsupported, position);
                parsed.moov_bytes.resize(static_cast<size_t>(size));
                if (!reader.read_exact(position, parsed.moov_bytes)) return fail(isobmff_reader_failure::io, position);
            } else if (type("mdat")) {
                parsed.mdats.push_back({position + header_size, position + size});
            } else if (type("moof") || type("mfra") || type("sidx")) {
                return fail(isobmff_reader_failure::unsupported, position);
            } else if (!type("free") && !type("skip") && !type("wide") && !type("uuid")) {
                // An unknown, completely bounded top-level box is opaque
                // metadata, not a sample-data owner. Target-specific source
                // binding checks run separately; never infer offsets from it.
                parsed.opaque_metadata = true;
            }
            position += size;
        }
        if (!have_moov || parsed.mdats.empty() || (require_ftyp && !have_ftyp)) return fail(isobmff_reader_failure::malformed, UINT64_MAX);
        if (!have_ftyp) parsed.quicktime = true;
        const std::span<const uint8_t> data(parsed.moov_bytes);
        const auto metadata_graph=[&](auto&& self,size_t first,size_t last,unsigned depth)->bool {
            if(depth>16)return false;
            for(size_t at=first;at<last;) {
                isobmff_box_header child{};if(!try_read_box_header(data.data(),at,last,child))return false;
                const auto kind=[&](const char* name){return type_is(data.data(),at,name);};
                if(kind("uuid")||kind("meta")||kind("udta")||kind("meco")||kind("tref")||kind("mpvd")||kind("sefd"))parsed.opaque_metadata=true;
                if(kind("moov")||kind("trak")||kind("mdia")||kind("minf")||kind("stbl")||kind("edts")||kind("dinf"))
                    if(!self(self,at+child.header_size,at+child.size,depth+1))return false;
                at+=child.size;
            }return true;
        };
        if(!metadata_graph(metadata_graph,0,data.size(),0))return fail(isobmff_reader_failure::malformed,moov_offset);
        isobmff_box_header moov{};
        if (!try_read_box_header(data.data(), 0, data.size(), moov) || moov.size != data.size()) return fail(isobmff_reader_failure::malformed, moov_offset);
        current_offset = moov_offset;
        isobmff_box_header mvex{}; size_t mvex_s = 0;
        if (!direct_child(data.data(), moov.header_size, data.size(), "mvex", mvex, mvex_s)) return fail(isobmff_reader_failure::malformed, moov_offset);
        if (mvex.size) return fail(isobmff_reader_failure::unsupported, moov_offset + mvex_s);
        lpb::media::isobmff_track_graph graph;
        if(!lpb::media::inspect_isobmff_track_graph(data,graph))return fail(isobmff_reader_failure::malformed,moov_offset);
        if(graph.opaque_relationships)parsed.opaque_metadata=true;
        std::vector<lpb::media::isobmff_metadata_key_track> metadata_tracks;
        if(!lpb::media::inspect_isobmff_metadata_keys(data,metadata_tracks))return fail(isobmff_reader_failure::malformed,moov_offset);
        size_t video_count = 0;
        std::vector<std::pair<uint64_t, uint64_t>> chunks;
        for (size_t p = moov.header_size; p < data.size();) {
            isobmff_box_header trak{};
            if (!try_read_box_header(data.data(), p, data.size(), trak)) return fail(isobmff_reader_failure::malformed, moov_offset + p);
            if (type_is(data.data(), p, "trak")) {
                current_offset = moov_offset + p;
                const size_t end = p + trak.size;
                isobmff_box_header mdia{}, hdlr{}, minf{}, stbl{}, stsd{}, stz2{}, mdhd{};
                size_t ms = 0, hs = 0, fs = 0, ss = 0, ds = 0, zs = 0, ts = 0;
                if (!direct_child(data.data(), p + trak.header_size, end, "mdia", mdia, ms) || !mdia.size ||
                    !direct_child(data.data(), ms + mdia.header_size, ms + mdia.size, "hdlr", hdlr, hs) || hdlr.size < hdlr.header_size + 12 ||
                    !direct_child(data.data(), ms + mdia.header_size, ms + mdia.size, "minf", minf, fs) || !minf.size ||
                    !direct_child(data.data(), fs + minf.header_size, fs + minf.size, "stbl", stbl, ss) || !stbl.size ||
                    !direct_child(data.data(), ms + mdia.header_size, ms + mdia.size, "mdhd", mdhd, ts) || !mdhd.size) return fail(isobmff_reader_failure::malformed, current_offset);
                uint32_t timescale = 0;
                if (!parse_media_header_timescale(data, ts, ts + mdhd.size, false, timescale)) return fail(isobmff_reader_failure::malformed, moov_offset + ts);
                const bool video = std::memcmp(data.data() + hs + hdlr.header_size + 8, "vide", 4) == 0;
                const bool auxiliary = std::memcmp(data.data() + hs + hdlr.header_size + 8, "auxv", 4) == 0;
                const bool audio = std::memcmp(data.data() + hs + hdlr.header_size + 8, "soun", 4) == 0;
                const bool metadata = std::memcmp(data.data()+hs+hdlr.header_size+8,"meta",4)==0;
                if (!video && !auxiliary && !audio && !metadata) return fail(isobmff_reader_failure::unsupported, current_offset);
                if (video && ++video_count != 1) return fail(isobmff_reader_failure::unsupported, current_offset);
                if (!direct_child(data.data(), ss + stbl.header_size, ss + stbl.size, "stz2", stz2, zs)) return fail(isobmff_reader_failure::malformed, current_offset);
                if (stz2.size) return fail(isobmff_reader_failure::unsupported, moov_offset + zs);
                isobmff_sample_table_layout table{};uint64_t table_bad{};
                if(!inspect_isobmff_strict_sample_table(data,ss,ss+stbl.size,parsed.mdats,0,table,table_bad))
                    return fail(isobmff_reader_failure::malformed,moov_offset+table_bad);
                for (const auto& chunk : table.chunks) chunks.emplace_back(chunk.offset, chunk.offset + chunk.size);
                if (!direct_child(data.data(), ss + stbl.header_size, ss + stbl.size, "stsd", stsd, ds)) return fail(isobmff_reader_failure::malformed, current_offset);
                if (table.sample_description_count != 1) return fail(isobmff_reader_failure::unsupported, moov_offset + ds);
                // Resolve data_reference_index=1 to an actual self-contained URL.
                isobmff_box_header dinf{}, dref{}; size_t di = 0, dr = 0;
                if (!direct_child(data.data(), fs + minf.header_size, fs + minf.size, "dinf", dinf, di) || !dinf.size ||
                    !direct_child(data.data(), di + dinf.header_size, di + dinf.size, "dref", dref, dr) || dref.size < dref.header_size + 8)
                    return fail(isobmff_reader_failure::malformed, current_offset);
                const size_t db = dr + dref.header_size;
                if (read_be32u(data.data() + db) != 0 || read_be32u(data.data() + db + 4) != 1)
                    return fail(isobmff_reader_failure::unsupported, moov_offset + dr);
                isobmff_box_header url{}; const size_t us = db + 8;
                if (!try_read_box_header(data.data(), us, dr + dref.size, url) || us + url.size != dr + dref.size ||
                    (!type_is(data.data(), us, "url ")&&!type_is(data.data(),us,"alis")) || url.size != url.header_size + 4 ||
                    read_be32u(data.data() + us + url.header_size) != 1)
                    return fail(isobmff_reader_failure::unsupported, moov_offset + us);
                const size_t entry_start = ds + stsd.header_size + 8;
                isobmff_box_header entry{};
                if (!try_read_box_header(data.data(), entry_start, ds + stsd.size, entry)) return fail(isobmff_reader_failure::malformed, moov_offset + entry_start);
                const size_t eb = entry_start + entry.header_size;
                if(metadata) {
                    if(type_is(data.data(),entry_start,"mebx")) {
                        const auto owner=std::find_if(metadata_tracks.begin(),metadata_tracks.end(),[&](const auto& track){return track.offset==p;});
                        if(owner==metadata_tracks.end()||owner->keys.empty())return fail(isobmff_reader_failure::malformed,moov_offset+entry_start);
                        const auto scope=lpb::media::declared_metadata_sample_scope(owner->keys.front().name_space,owner->keys.front().name);
                        lpb::media::metadata_sample_diagnostic metadata_error;
                        if(!lpb::media::validate_isobmff_metadata_key_samples_reader(data,*owner,reader,scope,
                            lpb::media::metadata_validation_purpose::read_only_media,&metadata_error))
                            return fail(metadata_error.io?isobmff_reader_failure::io:isobmff_reader_failure::malformed,
                                metadata_error.io?metadata_error.offset:moov_offset+entry_start);
                        parsed.opaque_metadata=true;parsed.sample_tables.push_back(std::move(table));p+=trak.size;continue;
                    }
                    // Explicit vendor mett syntax observed in actual >=X300
                    // files. Preserve opaque timed samples; they grant no
                    // target binding/timing authority. Table ranges, timing,
                    // references and cross-track overlap are still verified.
                    constexpr char mime[]="application/livePhoto-meta";
                    if(!type_is(data.data(),entry_start,"mett")||entry_start+entry.size!=ds+stsd.size||
                        entry.size-entry.header_size!=2*sizeof(mime)||std::memcmp(data.data()+eb,mime,sizeof(mime))||
                        std::memcmp(data.data()+eb+sizeof(mime),mime,sizeof(mime)))
                        return fail(isobmff_reader_failure::unsupported,moov_offset+entry_start);
                    parsed.opaque_metadata=true;parsed.sample_tables.push_back(std::move(table));p+=trak.size;continue;
                }
                if (entry.size < entry.header_size + 8 || read_be16u(data.data() + eb + 6) != 1) return fail(isobmff_reader_failure::unsupported, moov_offset + entry_start);
                if (video||auxiliary) {
                    uint32_t width{},height{},orientation{},codec{};
                    if (entry.size < entry.header_size + 78) return fail(isobmff_reader_failure::malformed, moov_offset + entry_start);
                    if (type_is(data.data(), entry_start, "avc1") || type_is(data.data(), entry_start, "avc3")) codec = 2;
                    else if (type_is(data.data(), entry_start, "hvc1") || type_is(data.data(), entry_start, "hev1")) codec = 3;
                    else return fail(isobmff_reader_failure::unsupported, moov_offset + entry_start);
                    width = read_be16u(data.data() + eb + 24);
                    height = read_be16u(data.data() + eb + 26);
                    if (!width || !height) return fail(isobmff_reader_failure::malformed, moov_offset + entry_start);
                    isobmff_box_header tkhd{}; size_t ks = 0;
                    if (!direct_child(data.data(), p + trak.header_size, end, "tkhd", tkhd, ks) || !tkhd.size || tkhd.size < tkhd.header_size + 4) return fail(isobmff_reader_failure::malformed, current_offset);
                    const size_t kb = ks + tkhd.header_size;
                    const uint8_t version = data[kb];
                    if (version > 1) return fail(isobmff_reader_failure::unsupported, moov_offset + ks);
                    const size_t matrix = kb + (version == 0 ? 40 : 52);
                    if (ks + tkhd.size - kb != (version == 0 ? 84U : 96U)) return fail(isobmff_reader_failure::malformed, moov_offset + ks);
                    const int64_t a = sign_extend_be32(read_be32u(data.data() + matrix));
                    const int64_t b = sign_extend_be32(read_be32u(data.data() + matrix + 4));
                    const int64_t c = sign_extend_be32(read_be32u(data.data() + matrix + 12));
                    const int64_t d = sign_extend_be32(read_be32u(data.data() + matrix + 16));
                    if (read_be32u(data.data() + matrix + 8) || read_be32u(data.data() + matrix + 20) || read_be32u(data.data() + matrix + 32) != 0x40000000U) return fail(isobmff_reader_failure::unsupported, moov_offset + matrix);
                    constexpr int32_t transforms[8][4] = {{1,0,0,1},{-1,0,0,1},{-1,0,0,-1},{1,0,0,-1},{0,1,1,0},{0,1,-1,0},{0,-1,-1,0},{0,-1,1,0}};
                    for (uint32_t i = 0; i < 8; ++i) if (a == transforms[i][0] * 65536LL && b == transforms[i][1] * 65536LL && c == transforms[i][2] * 65536LL && d == transforms[i][3] * 65536LL) orientation = i + 1;
                    if (!orientation) return fail(isobmff_reader_failure::unsupported, moov_offset + matrix);
                    const uint32_t width_fixed = read_be32u(data.data() + matrix + 36);
                    const uint32_t height_fixed = read_be32u(data.data() + matrix + 40);
                    if (width_fixed != (width << 16) || height_fixed != (height << 16)) return fail(isobmff_reader_failure::unsupported, moov_offset + matrix + 36);
                    if(video) {
                        parsed.video_width=width;parsed.video_height=height;parsed.video_orientation=orientation;parsed.video_codec=codec;
                    }else {
                        lpb::media::isobmff_video_profile auxiliary_profile;
                        if(!lpb::media::inspect_isobmff_visual_codec_configuration(data.subspan(entry_start,entry.size),auxiliary_profile))
                            return fail(isobmff_reader_failure::malformed,moov_offset+entry_start);
                        const auto owner=std::find_if(graph.tracks.begin(),graph.tracks.end(),[&](const auto& track){return track.offset==p;});
                        if(owner==graph.tracks.end())return fail(isobmff_reader_failure::malformed,current_offset);
                        parsed.auxiliary_video_tracks.push_back({owner->id,width,height,orientation,codec,table.sample_count});
                        parsed.opaque_metadata=true;
                    }
                } else {
                    if(type_is(data.data(),entry_start,"lpcm")) {
                        lpb::media::lpcm_v2_layout pcm;
                        if(!lpb::media::inspect_lpcm_v2(data.subspan(eb,entry.size-entry.header_size),pcm)||pcm.rate!=timescale)
                            return fail(isobmff_reader_failure::malformed,moov_offset+entry_start);
                        for(size_t at=eb+pcm.extension_offset;at<entry_start+entry.size;) {
                            isobmff_box_header extension{};
                            if(!try_read_box_header(data.data(),at,entry_start+entry.size,extension)||extension.extends_to_end)
                                return fail(isobmff_reader_failure::malformed,moov_offset+at);
                            parsed.opaque_metadata=true;at+=extension.size;
                        }
                        isobmff_box_header sizes{},timing{};size_t sz=0,tt=0;
                        if(!direct_child(data.data(),ss+stbl.header_size,ss+stbl.size,"stsz",sizes,sz)||!sizes.size||
                           !direct_child(data.data(),ss+stbl.header_size,ss+stbl.size,"stts",timing,tt)||!timing.size)
                            return fail(isobmff_reader_failure::malformed,current_offset);
                        const size_t sb=sz+sizes.header_size,tb=tt+timing.header_size;
                        const uint32_t fixed=read_be32u(data.data()+sb+4),count=read_be32u(data.data()+sb+8);
                        if(fixed?fixed!=pcm.bytes_per_packet:false)return fail(isobmff_reader_failure::malformed,moov_offset+sz);
                        if(!fixed)for(uint32_t i=0;i<count;++i)if(read_be32u(data.data()+sb+12+size_t(i)*4)!=pcm.bytes_per_packet)
                            return fail(isobmff_reader_failure::malformed,moov_offset+sz);
                        const uint32_t runs=read_be32u(data.data()+tb+4);
                        for(uint32_t i=0;i<runs;++i)if(read_be32u(data.data()+tb+12+size_t(i)*8)!=pcm.frames_per_packet)
                            return fail(isobmff_reader_failure::malformed,moov_offset+tt);
                        parsed.sample_tables.push_back(std::move(table));p+=trak.size;continue;
                    }
                    // Version-0 AAC entry; its ASC is structurally validated
                    // for LC and explicit LC+SBR/PS without changing payloads.
                    if (!type_is(data.data(), entry_start, "mp4a")) return fail(isobmff_reader_failure::unsupported, moov_offset + entry_start);
                    if (entry.size < entry.header_size + 28) return fail(isobmff_reader_failure::malformed, moov_offset + entry_start);
                    if (read_be16u(data.data() + eb + 8) != 0) return fail(isobmff_reader_failure::unsupported, moov_offset + entry_start);
                    if (!read_be16u(data.data() + eb + 16) || !read_be16u(data.data() + eb + 18) || !read_be32u(data.data() + eb + 24)) return fail(isobmff_reader_failure::malformed, moov_offset + entry_start);
                    isobmff_box_header esds{}; size_t es = 0;
                    if (!direct_child(data.data(), eb + 28, entry_start + entry.size, "esds", esds, es) || esds.size < esds.header_size + 8) return fail(isobmff_reader_failure::malformed, moov_offset + entry_start);
                    if (read_be32u(data.data() + es + esds.header_size) != 0) return fail(isobmff_reader_failure::unsupported, moov_offset + es);
                    // Descriptor lengths are variable-length integers, never raw tag searches.
                    const auto descriptor = [&](size_t& cursor, size_t limit, uint8_t tag, size_t& payload_end) {
                        if (cursor >= limit || data[cursor++] != tag) return false;
                        uint32_t length = 0;
                        for (unsigned n = 0; n < 4; ++n) {
                            if (cursor >= limit) return false;
                            const uint8_t byte = data[cursor++];
                            length = (length << 7) | (byte & 0x7fU);
                            if (!(byte & 0x80U)) {
                                if (length > limit - cursor) return false;
                                payload_end = cursor + length; return true;
                            }
                        }
                        return false;
                    };
                    size_t cursor = es + esds.header_size + 4, es_end = 0, config_end = 0, asc_end = 0;
                    if (!descriptor(cursor, es + esds.size, 3, es_end) || es_end - cursor < 3)
                        return fail(isobmff_reader_failure::malformed, moov_offset + es);
                    cursor += 2; const uint8_t flags = data[cursor++];
                    if (flags & 0xe0U) return fail(isobmff_reader_failure::unsupported, moov_offset + es);
                    if (!descriptor(cursor, es_end, 4, config_end) || config_end - cursor < 13)
                        return fail(isobmff_reader_failure::malformed, moov_offset + es);
                    if (data[cursor] != 0x40 || (data[cursor + 1] >> 2) != 5 || !(data[cursor + 1] & 1))
                        return fail(isobmff_reader_failure::unsupported, moov_offset + es);
                    cursor += 13;
                    if (!descriptor(cursor, config_end, 5, asc_end) || asc_end - cursor < 2)
                        return fail(isobmff_reader_failure::malformed, moov_offset + es);
                    bool audio_header_mismatch=false;
                    if(!lpb::media::aac_config_matches_entry(data.subspan(cursor,asc_end-cursor),
                        read_be16u(data.data()+eb+16),read_be32u(data.data()+eb+24),&audio_header_mismatch))
                        return fail(audio_header_mismatch?isobmff_reader_failure::malformed:isobmff_reader_failure::unsupported,
                            moov_offset+(audio_header_mismatch?entry_start:es));
                    if (asc_end != config_end) return fail(isobmff_reader_failure::unsupported, moov_offset + es);
                    cursor = config_end;
                    size_t sl_end = 0;
                    if (!descriptor(cursor, es_end, 6, sl_end) || sl_end - cursor != 1 || data[cursor] != 2 ||
                        sl_end != es_end || es_end != es + esds.size)
                        return fail(isobmff_reader_failure::unsupported, moov_offset + es);
                }
                parsed.sample_tables.push_back(std::move(table));
            }
            p += trak.size;
        }
        if (video_count != 1) return fail(isobmff_reader_failure::malformed, moov_offset);
        std::sort(chunks.begin(), chunks.end());
        for (size_t i = 1; i < chunks.size(); ++i) if (chunks[i-1].second > chunks[i].first) return fail(isobmff_reader_failure::malformed, chunks[i].first);
        isobmff_reader_failure timing_failure = isobmff_reader_failure::malformed;
        if (!inspect_timeline_moov(data, moov, 0, data.size(), parsed.mdats, 0, parsed.quicktime,
                parsed.timeline, &timing_failure)) return fail(timing_failure, moov_offset);
        output = std::move(parsed);
        return true;
    } catch (...) {
        return fail(isobmff_reader_failure::unsupported, current_offset);
    }
}
