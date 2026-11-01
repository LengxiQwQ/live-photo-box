#include "containers/isobmff_mebx_transplant.h"
#include "containers/remux_byte_proof.h"
#include "containers/isobmff_track_graph.h"
#include "containers/isobmff_video_profile.h"
#include "containers/inline_transform_metadata.h"
#include "containers/inline_scene_metadata.h"

#include "containers/isobmff.h"
#include "foundation/sha256_core.h"

#include <algorithm>
#include <array>
#include <cstring>
#include <limits>
#include <numeric>
#include <string_view>

namespace lpb::media {
namespace {

uint32_t read_u32(const uint8_t* p) noexcept {
    return (static_cast<uint32_t>(p[0]) << 24U) |
        (static_cast<uint32_t>(p[1]) << 16U) |
        (static_cast<uint32_t>(p[2]) << 8U) |
        static_cast<uint32_t>(p[3]);
}

uint64_t read_u64(const uint8_t* p) noexcept {
    return (static_cast<uint64_t>(read_u32(p)) << 32U) | read_u32(p + 4);
}

void write_u32(uint8_t* p, uint32_t value) noexcept {
    p[0] = static_cast<uint8_t>(value >> 24U);
    p[1] = static_cast<uint8_t>(value >> 16U);
    p[2] = static_cast<uint8_t>(value >> 8U);
    p[3] = static_cast<uint8_t>(value);
}

void write_u64(uint8_t* p, uint64_t value) noexcept {
    write_u32(p, static_cast<uint32_t>(value >> 32U));
    write_u32(p + 4, static_cast<uint32_t>(value));
}

bool box_type(std::span<const uint8_t> data, size_t start, const char type[4]) noexcept {
    return start <= data.size() && data.size() - start >= 8 &&
        std::memcmp(data.data() + start + 4, type, 4) == 0;
}

bool unique_child(std::span<const uint8_t> data, size_t start, size_t end,
    const char type[4], isobmff_box_header& result, size_t& result_start,
    bool required = true) noexcept {
    if (start > end || end > data.size()) return false;
    result = {};
    result_start = 0;
    size_t position = start;
    while (position < end) {
        isobmff_box_header child{};
        if (!try_read_box_header(data.data(), position, end, child)) return false;
        if (box_type(data, position, type)) {
            if (result.size != 0) return false;
            result = child;
            result_start = position;
        }
        position += child.size;
    }
    return position == end && (!required || result.size != 0);
}

struct track_record {
    size_t start{};
    size_t end{};
    size_t stbl_start{};
    size_t stbl_box_start{};
    size_t stbl_end{};
    size_t sample_entry_offset{};
    uint32_t track_id{};
    uint32_t time_scale{};
    uint64_t media_duration{};
    char handler[4]{};
    bool is_mebx{};
    isobmff_sample_table_layout sample_table{};
    size_t reference_id_offset{};
    size_t dependency_id_offset{};
};

struct file_record {
    size_t moov_start{};
    size_t moov_end{};
    size_t moov_header_size{};
    uint32_t movie_time_scale{};
    uint64_t movie_duration{};
    std::vector<isobmff_media_data_range> mdats;
    std::vector<track_record> tracks;
};

bool quicktime_file(std::span<const uint8_t> data, bool& quicktime) {
    size_t position = 0U; bool found = false; quicktime = false;
    while (position < data.size()) {
        isobmff_box_header box{};
        if (!try_read_box_header(data.data(), position, data.size(), box)) return false;
        if (box_type(data, position, "ftyp")) {
            if (found || box.size < box.header_size + 8U || (box.size - box.header_size) % 4U != 0U) return false;
            found = true; quicktime = std::memcmp(data.data() + position + box.header_size, "qt  ", 4U) == 0;
        }
        position += box.size;
    }
    return found;
}

// Composition offsets follow the same major-or-compatible QT predicate as
// the strict timeline parser. Handler string representation remains separate.
bool quicktime_composition_file(std::span<const uint8_t> data,bool& quicktime) {
    bool major=false;if(!quicktime_file(data,major))return false;quicktime=major;
    for(size_t p=0;p<data.size();) {
        isobmff_box_header box{};if(!try_read_box_header(data.data(),p,data.size(),box))return false;
        if(box_type(data,p,"ftyp"))for(size_t at=p+box.header_size+8U;at<p+box.size;at+=4U)
            if(std::memcmp(data.data()+at,"qt  ",4U)==0)quicktime=true;
        p+=box.size;
    }
    return true;
}

// The container's authoritative ftyp chooses Pascal (QuickTime) or NUL (ISO),
// never a heuristic based on the first name byte. Only exact, same-size forms
// are supported here; no padding, embedded terminator or ambiguous length.
bool handler_name_bytes(std::span<const uint8_t> data, const isobmff_box_header& box,
    bool source_quicktime, bool target_quicktime, std::vector<uint8_t>& result) {
    if (box.size < box.header_size + 24U || read_u32(data.data() + box.start + box.header_size) != 0U) return false;
    const size_t start = box.start + box.header_size + 24U, count = box.size - box.header_size - 24U;
    size_t text_start = start, length = 0U;
    if (count != 0U) {
        if (count > 256U) return false;
        if (source_quicktime) {
            length = data[start]; text_start = start + 1U;
            if (length + 1U != count) return false;
        } else {
            if (data[start + count - 1U] != 0U) return false;
            length = count - 1U;
        }
        if (std::find(data.begin() + text_start, data.begin() + text_start + length, 0U) != data.begin() + text_start + length) return false;
    }
    result.assign(data.begin() + box.start, data.begin() + start);
    if (count != 0U) {
        if (target_quicktime) result.push_back(static_cast<uint8_t>(length));
        result.insert(result.end(), data.begin() + text_start, data.begin() + text_start + length);
        if (!target_quicktime) result.push_back(0U);
    }
    return result.size() == box.size;
}

bool track_handlers(std::span<const uint8_t> data, const track_record& track,
    std::array<isobmff_box_header, 2>& handlers) {
    isobmff_box_header mdia{}, minf{}; size_t p = 0U;
    return unique_child(data, track.start + 8U, track.end, "mdia", mdia, p) &&
        unique_child(data, mdia.start + mdia.header_size, mdia.start + mdia.size, "hdlr", handlers[0], p) &&
        unique_child(data, mdia.start + mdia.header_size, mdia.start + mdia.size, "minf", minf, p) &&
        unique_child(data, minf.start + minf.header_size, minf.start + minf.size, "hdlr", handlers[1], p, false);
}

bool normalize_track_handler_names(std::span<const uint8_t> data, const track_record& track,
    bool actual_quicktime, bool expected_quicktime, std::vector<uint8_t>& normalized) {
    std::array<isobmff_box_header, 2> handlers{};
    if (!track_handlers(data, track, handlers)) return false;
    for (const auto& handler : handlers) {
        if (handler.size == 0U) continue;
        std::vector<uint8_t> bytes;
        if (!handler_name_bytes(data, handler, actual_quicktime, expected_quicktime, bytes) ||
            handler.start < track.start || handler.start - track.start > normalized.size() ||
            bytes.size() > normalized.size() - (handler.start - track.start)) return false;
        std::memcpy(normalized.data() + handler.start - track.start, bytes.data(), bytes.size());
    }
    return true;
}

// A mapped track contains source-owned handlers and may also contain a new
// target-owned data handler. Only source-owned names need representation mapping.
bool normalize_mapped_track_handler_names(std::span<const uint8_t> source,const track_record& original,
    std::vector<uint8_t>& mapped,bool source_qt,bool target_qt) {
    std::array<isobmff_box_header,2> before{},after{};track_record local{};local.end=mapped.size();
    if(!track_handlers(source,original,before)||!track_handlers(mapped,local,after))return false;
    for(size_t i=0;i<before.size();++i) {
        if(!before[i].size)continue;
        std::vector<uint8_t> bytes;
        if(!handler_name_bytes(source,before[i],source_qt,target_qt,bytes)||bytes.size()!=after[i].size||
            after[i].start>mapped.size()||bytes.size()>mapped.size()-after[i].start)return false;
        std::memcpy(mapped.data()+after[i].start,bytes.data(),bytes.size());
    }
    return true;
}

bool read_track_id(std::span<const uint8_t> data, const isobmff_box_header& tkhd,
    uint32_t& track_id) noexcept {
    const size_t body = tkhd.start + tkhd.header_size;
    if (tkhd.size < tkhd.header_size + 20U) return false;
    const uint8_t version = data[body];
    size_t offset = 0;
    if (version == 0U) offset = body + 12U;
    else if (version == 1U) offset = body + 20U;
    else return false;
    if (offset > tkhd.start + tkhd.size || tkhd.start + tkhd.size - offset < 4U) return false;
    track_id = read_u32(data.data() + offset);
    return track_id != 0U;
}

bool read_media_timing(std::span<const uint8_t> data, const isobmff_box_header& mdhd,
    uint32_t& time_scale, uint64_t& duration) noexcept {
    const size_t body = mdhd.start + mdhd.header_size;
    if (mdhd.size < mdhd.header_size + 24U) return false;
    const uint8_t version = data[body];
    size_t scale_offset = 0;
    size_t duration_offset = 0;
    if (version == 0U) {
        scale_offset = body + 12U;
        duration_offset = body + 16U;
    } else if (version == 1U) {
        if (mdhd.size < mdhd.header_size + 32U) return false;
        scale_offset = body + 20U;
        duration_offset = body + 24U;
    } else {
        return false;
    }
    const size_t end = mdhd.start + mdhd.size;
    const size_t duration_size = version == 0U ? 4U : 8U;
    if (scale_offset > end || end - scale_offset < 4U ||
        duration_offset > end || end - duration_offset < duration_size) return false;
    time_scale = read_u32(data.data() + scale_offset);
    duration = version == 0U ? read_u32(data.data() + duration_offset) : read_u64(data.data() + duration_offset);
    return time_scale != 0U;
}

bool read_movie_timing(std::span<const uint8_t> data, const isobmff_box_header& mvhd,
    uint32_t& time_scale, uint64_t& duration) noexcept {
    const size_t body = mvhd.start + mvhd.header_size;
    if (mvhd.size < mvhd.header_size + 20U) return false;
    const uint8_t version = data[body];
    size_t scale_offset = 0;
    size_t duration_offset = 0;
    size_t duration_size = 0;
    if (version == 0U) {
        scale_offset = body + 12U;
        duration_offset = body + 16U;
        duration_size = 4U;
    } else if (version == 1U) {
        if (mvhd.size < mvhd.header_size + 32U) return false;
        scale_offset = body + 20U;
        duration_offset = body + 24U;
        duration_size = 8U;
    } else {
        return false;
    }
    const size_t end = mvhd.start + mvhd.size;
    if (scale_offset > end || end - scale_offset < 4U ||
        duration_offset > end || end - duration_offset < duration_size) return false;
    time_scale = read_u32(data.data() + scale_offset);
    duration = version == 0U
        ? read_u32(data.data() + duration_offset)
        : read_u64(data.data() + duration_offset);
    return time_scale != 0U && duration != 0U;
}

struct movie_duration_field {
    size_t offset{};
    size_t width{};
};

bool collect_track_movie_duration_fields(std::span<const uint8_t> track_data,
    std::vector<movie_duration_field>& fields) {
    fields.clear();
    isobmff_box_header trak{};
    if (!box_type(track_data, 0U, "trak") ||
        !try_read_box_header(track_data.data(), 0U, track_data.size(), trak) ||
        trak.size != track_data.size()) return false;

    isobmff_box_header tkhd{};
    size_t tkhd_start = 0U;
    if (!unique_child(track_data, trak.header_size, trak.size, "tkhd", tkhd, tkhd_start)) return false;
    const size_t tkhd_body = tkhd_start + tkhd.header_size;
    size_t tkhd_duration_offset = 0U;
    size_t tkhd_duration_width = 0U;
    if (tkhd.size < tkhd.header_size + 24U) return false;
    if (track_data[tkhd_body] == 0U) {
        tkhd_duration_offset = tkhd_body + 20U;
        tkhd_duration_width = 4U;
    } else if (track_data[tkhd_body] == 1U) {
        if (tkhd.size < tkhd.header_size + 32U) return false;
        tkhd_duration_offset = tkhd_body + 28U;
        tkhd_duration_width = 8U;
    } else {
        return false;
    }
    const size_t tkhd_end = tkhd_start + tkhd.size;
    if (tkhd_duration_offset > tkhd_end || tkhd_end - tkhd_duration_offset < tkhd_duration_width) return false;
    fields.push_back({tkhd_duration_offset, tkhd_duration_width});

    isobmff_box_header edts{};
    size_t edts_start = 0U;
    if (!unique_child(track_data, trak.header_size, trak.size, "edts", edts, edts_start, false)) return false;
    if (edts.size == 0U) return true;

    isobmff_box_header elst{};
    size_t elst_start = 0U;
    const size_t edts_end = edts_start + edts.size;
    if (!unique_child(track_data, edts_start + edts.header_size, edts_end, "elst", elst, elst_start)) return false;
    const size_t elst_body = elst_start + elst.header_size;
    if (elst.size < elst.header_size + 8U) return false;
    const uint8_t version = track_data[elst_body];
    const size_t entry_size = version == 0U ? 12U : version == 1U ? 20U : 0U;
    const size_t duration_width = version == 0U ? 4U : version == 1U ? 8U : 0U;
    if (entry_size == 0U) return false;
    const uint32_t entry_count = read_u32(track_data.data() + elst_body + 4U);
    const size_t entries_start = elst_body + 8U;
    const size_t elst_end = elst_start + elst.size;
    if (entries_start > elst_end) return false;
    const size_t entries_bytes = elst_end - entries_start;
    if (entry_count > entries_bytes / entry_size ||
        static_cast<size_t>(entry_count) * entry_size != entries_bytes) return false;
    fields.reserve(fields.size() + entry_count);
    for (uint32_t index = 0U; index < entry_count; ++index) {
        fields.push_back({entries_start + static_cast<size_t>(index) * entry_size, duration_width});
    }
    return true;
}

bool rescale_movie_duration(uint64_t value, uint32_t source_time_scale,
    uint32_t target_time_scale, uint64_t& result) noexcept {
    if (source_time_scale == 0U || target_time_scale == 0U) return false;
    const uint64_t divisor = std::gcd(static_cast<uint64_t>(source_time_scale),
        static_cast<uint64_t>(target_time_scale));
    const uint64_t multiplier = static_cast<uint64_t>(target_time_scale) / divisor;
    const uint64_t denominator = static_cast<uint64_t>(source_time_scale) / divisor;
    if (value > (std::numeric_limits<uint64_t>::max)() / multiplier) return false;
    const uint64_t numerator = value * multiplier;
    uint64_t scaled = numerator / denominator;
    const uint64_t remainder = numerator % denominator;
    if (remainder >= denominator / 2U + denominator % 2U) {
        if (scaled == (std::numeric_limits<uint64_t>::max)()) return false;
        ++scaled;
    }
    result = scaled;
    return true;
}

// Quotient/remainder rescaling never forms value * scale. The remainder
// product fits uint64 because both timescales are uint32.
bool rescale_duration_ceil(uint64_t value, uint32_t source_time_scale,
    uint32_t target_time_scale, uint64_t& result) noexcept {
    if (!source_time_scale || !target_time_scale) return false;
    const uint64_t quotient = value / source_time_scale;
    const uint64_t remainder = value % source_time_scale;
    if (quotient > UINT64_MAX / target_time_scale) return false;
    const uint64_t tail = remainder * target_time_scale;
    const uint64_t extra = tail / source_time_scale + (tail % source_time_scale != 0U);
    const uint64_t whole = quotient * target_time_scale;
    if (whole > UINT64_MAX - extra) return false;
    result = whole + extra;
    return true;
}

bool rescale_signed_time(int64_t value, uint32_t source_time_scale,
    uint32_t target_time_scale, int64_t& result) noexcept {
    if (!source_time_scale || !target_time_scale) return false;
    const bool negative = value < 0;
    const uint64_t magnitude = negative ? uint64_t(-(value + 1)) + 1U : uint64_t(value);
    const uint64_t quotient = magnitude / source_time_scale;
    const uint64_t remainder = magnitude % source_time_scale;
    const uint64_t limit = uint64_t(INT64_MAX) + uint64_t(negative);
    if (quotient > limit / target_time_scale) return false;
    const uint64_t whole = quotient * target_time_scale;
    const uint64_t extra = (remainder * target_time_scale) / source_time_scale;
    if (whole > limit - extra) return false;
    const uint64_t scaled = whole + extra;
    result = negative ? (scaled == uint64_t(INT64_MAX) + 1U ? INT64_MIN : -int64_t(scaled)) : int64_t(scaled);
    return true;
}

bool safe_add_i64(int64_t a, int64_t b, int64_t& result) noexcept {
    if ((b > 0 && a > (std::numeric_limits<int64_t>::max)() - b) ||
        (b < 0 && a < (std::numeric_limits<int64_t>::min)() - b)) return false;
    result = a + b;
    return true;
}

bool safe_sub_i64(int64_t a, int64_t b, int64_t& result) noexcept {
    if ((b > 0 && a < (std::numeric_limits<int64_t>::min)() + b) ||
        (b < 0 && a > (std::numeric_limits<int64_t>::max)() + b)) return false;
    result = a - b;
    return true;
}

bool safe_add_u64(uint64_t a, uint64_t b, uint64_t& result) noexcept {
    if (a > (std::numeric_limits<uint64_t>::max)() - b) return false;
    result = a + b;
    return true;
}

uint64_t read_movie_duration_field(const uint8_t* data, size_t width) noexcept {
    return width == 4U ? read_u32(data) : read_u64(data);
}

bool write_movie_duration_field(uint8_t* data, size_t width, uint64_t value) noexcept {
    if (width == 4U) {
        if (value > UINT32_MAX) return false;
        write_u32(data, static_cast<uint32_t>(value));
        return true;
    }
    if (width == 8U) {
        write_u64(data, value);
        return true;
    }
    return false;
}

bool rebase_track_movie_durations(std::vector<uint8_t>& track_data,
    uint32_t source_time_scale, uint32_t target_time_scale) {
    std::vector<movie_duration_field> fields;
    if (!collect_track_movie_duration_fields(track_data, fields)) return false;
    for (const auto& field : fields) {
        if (field.offset > track_data.size() || track_data.size() - field.offset < field.width) return false;
        const uint64_t original = read_movie_duration_field(track_data.data() + field.offset, field.width);
        uint64_t rebased = 0U;
        if (!rescale_movie_duration(original, source_time_scale, target_time_scale, rebased) ||
            !write_movie_duration_field(track_data.data() + field.offset, field.width, rebased)) return false;
    }
    return true;
}

bool normalize_rebased_track_movie_durations(std::span<const uint8_t> source_track,
    std::span<const uint8_t> output_track, std::vector<uint8_t>& normalized_output_track,
    uint32_t source_time_scale, uint32_t output_time_scale) {
    std::vector<movie_duration_field> source_fields;
    std::vector<movie_duration_field> output_fields;
    if (!collect_track_movie_duration_fields(source_track, source_fields) ||
        !collect_track_movie_duration_fields(output_track, output_fields) ||
        source_fields.size() != output_fields.size() ||
        normalized_output_track.size() != output_track.size()) return false;
    for (size_t index = 0U; index < source_fields.size(); ++index) {
        const auto& source_field = source_fields[index];
        const auto& output_field = output_fields[index];
        if (source_field.offset != output_field.offset || source_field.width != output_field.width ||
            source_field.offset > source_track.size() || source_track.size() - source_field.offset < source_field.width ||
            output_field.offset > output_track.size() || output_track.size() - output_field.offset < output_field.width) return false;
        const uint64_t source_duration = read_movie_duration_field(source_track.data() + source_field.offset, source_field.width);
        const uint64_t output_duration = read_movie_duration_field(output_track.data() + output_field.offset, output_field.width);
        uint64_t expected_duration = 0U;
        if (!rescale_movie_duration(source_duration, source_time_scale, output_time_scale, expected_duration) ||
            output_duration != expected_duration ||
            !write_movie_duration_field(normalized_output_track.data() + source_field.offset,
                source_field.width, source_duration)) return false;
    }
    return true;
}

bool parse_sample_descriptions(std::span<const uint8_t> data, size_t stbl_start, size_t stbl_end,
    bool& is_mebx, size_t& mebx_entry_offset, uint32_t& description_count) noexcept {
    isobmff_box_header stsd{};
    size_t stsd_start = 0;
    if (!unique_child(data, stbl_start, stbl_end, "stsd", stsd, stsd_start)) return false;
    const size_t body = stsd_start + stsd.header_size;
    if (stsd.size < stsd.header_size + 8U || data[body] != 0U) return false;
    description_count = read_u32(data.data() + body + 4U);
    if (description_count == 0U || description_count > 1024U) return false;
    size_t position = body + 8U;
    const size_t end = stsd_start + stsd.size;
    is_mebx = false;
    mebx_entry_offset = 0;
    for (uint32_t index = 0; index < description_count; ++index) {
        isobmff_box_header entry{};
        if (!try_read_box_header(data.data(), position, end, entry)) return false;
        if (box_type(data, position, "mebx")) {
            if (is_mebx) return false;
            is_mebx = true;
            mebx_entry_offset = position;
        }
        position += entry.size;
    }
    return position == end;
}

bool local_data_reference(std::span<const uint8_t> data,const isobmff_box_header& minf,bool explicit_sizes=false) {
    isobmff_box_header dinf{}, dref{};
    size_t ds = 0U, rs = 0U;
    if (!unique_child(data, minf.start + minf.header_size, minf.start + minf.size, "dinf", dinf, ds) ||
        !unique_child(data, ds + dinf.header_size, ds + dinf.size, "dref", dref, rs) ||
        dinf.size != dinf.header_size + dref.size || dref.size < dref.header_size + 8U) return false;
    if(explicit_sizes&&(read_u32(data.data()+dinf.start)==0U||read_u32(data.data()+dref.start)==0U))return false;
    const size_t body = rs + dref.header_size;
    if (read_u32(data.data() + body) != 0U || read_u32(data.data() + body + 4U) != 1U) return false;
    const size_t start = body + 8U;
    isobmff_box_header entry{};
    return try_read_box_header(data.data(), start, rs + dref.size, entry) &&
        (box_type(data, start, "url ") || box_type(data, start, "alis")) &&
        entry.size == entry.header_size + 4U && start + entry.size == rs + dref.size &&
        (!explicit_sizes||read_u32(data.data()+entry.start)!=0U)&&
        read_u32(data.data() + start + entry.header_size) == 1U;
}

bool self_contained_reference(std::span<const uint8_t> data, const isobmff_box_header& minf,
    const isobmff_box_header& codec, bool allow_implicit_local_metadata = false) {
    return codec.size>=codec.header_size+8U&&data[codec.start+codec.header_size+6U]==0U&&
        (data[codec.start+codec.header_size+7U]==1U||
            (allow_implicit_local_metadata&&box_type(data,codec.start,"mebx")&&data[codec.start+codec.header_size+7U]==0U))&&
        local_data_reference(data,minf);
}

bool parse_track(std::span<const uint8_t> data, size_t trak_start, size_t trak_end,
    std::span<const isobmff_media_data_range> mdats, track_record& output) noexcept {
    output = {};
    if (!box_type(data, trak_start, "trak") || trak_start > trak_end || trak_end > data.size()) return false;
    isobmff_box_header trak{};
    if (!try_read_box_header(data.data(), trak_start, trak_end, trak) || trak_start + trak.size != trak_end) return false;
    output.start = trak_start;
    output.end = trak_end;

    isobmff_box_header tkhd{}, mdia{};
    size_t tkhd_start = 0, mdia_start = 0;
    if (!unique_child(data, trak_start + trak.header_size, trak_end, "tkhd", tkhd, tkhd_start) ||
        !unique_child(data, trak_start + trak.header_size, trak_end, "mdia", mdia, mdia_start) ||
        !read_track_id(data, tkhd, output.track_id)) return false;

    const size_t mdia_end = mdia_start + mdia.size;
    isobmff_box_header hdlr{}, mdhd{}, minf{};
    size_t hdlr_start = 0, mdhd_start = 0, minf_start = 0;
    if (!unique_child(data, mdia_start + mdia.header_size, mdia_end, "hdlr", hdlr, hdlr_start) ||
        !unique_child(data, mdia_start + mdia.header_size, mdia_end, "mdhd", mdhd, mdhd_start) ||
        !unique_child(data, mdia_start + mdia.header_size, mdia_end, "minf", minf, minf_start) ||
        !read_media_timing(data, mdhd, output.time_scale, output.media_duration) ||
        hdlr.size < hdlr.header_size + 12U) return false;
    const size_t handler_offset = hdlr_start + hdlr.header_size + 8U;
    std::memcpy(output.handler, data.data() + handler_offset, 4U);
    const size_t minf_end = minf_start + minf.size;
    isobmff_box_header stbl{};
    size_t stbl_start = 0;
    if (!unique_child(data, minf_start + minf.header_size, minf_end, "stbl", stbl, stbl_start)) return false;
    output.stbl_start = stbl_start + stbl.header_size;
    output.stbl_box_start = stbl_start;
    output.stbl_end = stbl_start + stbl.size;

    if (output.stbl_start == output.stbl_end && output.media_duration == 0U &&
        std::memcmp(output.handler,"soun",4U)==0) return true;
    uint32_t description_count = 0;
    if (!parse_sample_descriptions(data, output.stbl_start, output.stbl_end,
            output.is_mebx, output.sample_entry_offset, description_count)) return false;
    if (!output.is_mebx) return true;
    isobmff_box_header mebx_entry{};
    if (std::memcmp(output.handler, "meta", 4U) != 0 || description_count != 1U ||
        !try_read_box_header(data.data(), output.sample_entry_offset, output.stbl_end, mebx_entry) ||
        mebx_entry.size < mebx_entry.header_size + 8U ||
        !std::all_of(data.begin() + mebx_entry.start + mebx_entry.header_size,
            data.begin() + mebx_entry.start + mebx_entry.header_size + 6U, [](uint8_t value) { return value == 0U; }) ||
        !self_contained_reference(data, minf, mebx_entry, true)) return false;
    // Huawei's observed MEBX entry uses zero as an implicit local reference.
    // This exception is metadata-only: the singleton self-contained dref above
    // and every actual local mdat chunk below are still mandatory. A/V entries
    // and any explicit nonlocal/unknown reference never use this exception.
    if (std::memcmp(output.handler, "meta", 4U) != 0 || description_count != 1U ||
        !read_media_timing(data, mdhd, output.time_scale, output.media_duration) ||
        !inspect_isobmff_sample_table(data, output.stbl_start, output.stbl_end,
            mdats, 0U, output.sample_table)) return false;

    isobmff_box_header tref{};
    size_t tref_start = 0;
    if (!unique_child(data, trak_start + trak.header_size, trak_end, "tref", tref, tref_start)) return false;
    if (tref.size == 0U) return false;
    const size_t tref_end = tref_start + tref.size;
    isobmff_box_header cdsc{};
    size_t cdsc_start = 0;
    isobmff_box_header cdep{};
    size_t cdep_start=0;
    if (!unique_child(data, tref_start + tref.header_size, tref_end, "cdsc", cdsc, cdsc_start) ||
        cdsc.size != cdsc.header_size + 4U ||
        !unique_child(data,tref_start+tref.header_size,tref_end,"cdep",cdep,cdep_start,false) ||
        tref.size != tref.header_size + cdsc.size + cdep.size) return false;
    output.reference_id_offset = cdsc_start + cdsc.header_size;
    if(cdep.size) {
        if(cdep.size!=cdep.header_size+4U||read_u32(data.data()+cdep_start+cdep.header_size)!=
            read_u32(data.data()+output.reference_id_offset))return false;
        output.dependency_id_offset=cdep_start+cdep.header_size;
    }
    return true;
}

bool parse_file(std::span<const uint8_t> data, file_record& output,
    std::span<const isobmff_media_data_range> real_mdats = {}) noexcept {
    output = {};
    if (data.size() < 16U) return false;
    try {
        size_t position = 0;
        size_t moov_count = 0;
        while (position < data.size()) {
            isobmff_box_header box{};
            if (!try_read_box_header(data.data(), position, data.size(), box)) return false;
            if (box_type(data, position, "moov")) {
                if (++moov_count != 1U) return false;
                output.moov_start = position;
                output.moov_end = position + box.size;
                output.moov_header_size = box.header_size;
            } else if (box_type(data, position, "mdat")) {
                output.mdats.push_back({position + box.header_size, position + box.size});
            } else if (box_type(data, position, "moof")) {
                return false;
            }
            position += box.size;
        }
        if (position != data.size() || moov_count != 1U || output.mdats.empty()) return false;
        if (!real_mdats.empty()) output.mdats.assign(real_mdats.begin(), real_mdats.end());
        isobmff_box_header mvhd{};
        size_t mvhd_start = 0U;
        if (!unique_child(data, output.moov_start + output.moov_header_size,
                output.moov_end, "mvhd", mvhd, mvhd_start) ||
            !read_movie_timing(data, mvhd, output.movie_time_scale, output.movie_duration)) return false;
        size_t child = output.moov_start + output.moov_header_size;
        while (child < output.moov_end) {
            isobmff_box_header box{};
            if (!try_read_box_header(data.data(), child, output.moov_end, box)) return false;
            if (box_type(data, child, "trak")) {
                track_record track{};
                if (!parse_track(data, child, child + box.size, output.mdats, track)) return false;
                if (std::any_of(output.tracks.begin(), output.tracks.end(), [&](const track_record& prior) {
                        return prior.track_id == track.track_id;
                    })) return false;
                output.tracks.push_back(std::move(track));
            }
            child += box.size;
        }
        return child == output.moov_end && !output.tracks.empty();
    } catch (...) {
        output = {};
        return false;
    }
}

bool same_copy_track_graph(std::span<const uint8_t> source,const file_record& a,
    std::span<const uint8_t> output,const file_record& b) {
    isobmff_track_graph x{},y{};
    if(!inspect_isobmff_track_graph(source.subspan(a.moov_start,a.moov_end-a.moov_start),x)||
        !inspect_isobmff_track_graph(output.subspan(b.moov_start,b.moov_end-b.moov_start),y)||
        x.tracks.size()!=y.tracks.size())return false;
    for(size_t i=0;i<x.tracks.size();++i) {
        const auto& s=x.tracks[i];const auto& t=y.tracks[i];
        if(s.id!=t.id||s.handler!=t.handler||s.references.size()!=t.references.size())return false;
        for(size_t j=0;j<s.references.size();++j)
            if(s.references[j].kind!=t.references[j].kind||s.references[j].targets!=t.references[j].targets)return false;
    }
    return true;
}

bool patch_handler_names(std::span<const uint8_t> source, const file_record& original,
    std::vector<uint8_t>& output, const file_record& actual, bool metadata_only, bool same_track_ids=false) {
    bool source_qt = false, target_qt = false;
    if (!quicktime_file(source, source_qt) || !quicktime_file(output, target_qt)) return false;
    if(same_track_ids) {
        if(original.tracks.size()!=actual.tracks.size()||!same_copy_track_graph(source,original,output,actual))return false;
        for(size_t i=0;i<original.tracks.size();++i)
            if(original.tracks[i].track_id!=actual.tracks[i].track_id||
                std::memcmp(original.tracks[i].handler,actual.tracks[i].handler,4U)!=0)return false;
    }
    for (const auto& track : actual.tracks) {
        if (metadata_only && !track.is_mebx) continue;
        const track_record* before = nullptr;
        for (const auto& candidate : original.tracks) {
            if (std::memcmp(candidate.handler, track.handler, 4U) != 0 ||
                ((track.is_mebx||same_track_ids) && candidate.track_id != track.track_id)) continue;
            if (before != nullptr) return false;
            before = &candidate;
        }
        if (before == nullptr) return false;
        std::array<isobmff_box_header, 2> source_handlers{}, output_handlers{};
        if (!track_handlers(source, *before, source_handlers) || !track_handlers(output, track, output_handlers)) return false;
        for (size_t index = 0U; index < source_handlers.size(); ++index) {
            if (source_handlers[index].size == 0U) continue;
            std::vector<uint8_t> mapped;
            if (!handler_name_bytes(source, source_handlers[index], source_qt, target_qt, mapped) ||
                mapped.size() != output_handlers[index].size) return false;
            std::memcpy(output.data() + output_handlers[index].start, mapped.data(), mapped.size());
        }
    }
    return true;
}

bool inspect_mebx_tracks(std::span<const uint8_t> data, file_record& file,
    std::vector<track_record*>& metadata, track_record*& video_track) {
    metadata.clear();
    video_track = nullptr;
    size_t video_count = 0;
    for (auto& track : file.tracks) {
        if (track.is_mebx) {
            metadata.push_back(&track);
        }
        if (std::memcmp(track.handler, "vide", 4U) == 0) {
            video_track = &track;
            ++video_count;
        }
    }
    if (metadata.empty()) return true;
    if (video_count != 1U || video_track == nullptr) return false;
    bool quicktime = false;
    if (!quicktime_file(data, quicktime)) return false;
    for (const auto* track : metadata) {
        std::array<isobmff_box_header, 2> handlers{};
        if (!track_handlers(data, *track, handlers)) return false;
        for (const auto& handler : handlers) if (handler.size != 0U) {
            std::vector<uint8_t> canonical;
            if (!handler_name_bytes(data, handler, quicktime, false, canonical)) return false;
        }
        if (read_u32(data.data() + track->reference_id_offset) != video_track->track_id) return false;
        uint64_t payload_bytes = 0;
        for (const auto& chunk : track->sample_table.chunks) {
            if (payload_bytes > (std::numeric_limits<uint64_t>::max)() - chunk.size) return false;
            payload_bytes += chunk.size;
        }
        if (payload_bytes == 0U || track->sample_table.sample_count == 0U) return false;
    }
    return true;
}

bool inspect_mebx(std::span<const uint8_t> data, file_record& file,
    track_record*& mebx_track, track_record*& video_track) {
    std::vector<track_record*> metadata;
    mebx_track = nullptr;
    if (!inspect_mebx_tracks(data, file, metadata, video_track) || metadata.size() > 1U) return false;
    if (!metadata.empty()) mebx_track = metadata.front();
    return true;
}

isobmff_mebx_track_facts facts_for(const track_record& track, uint32_t video_id) noexcept {
    uint64_t payload_bytes = 0;
    for (const auto& chunk : track.sample_table.chunks) payload_bytes += chunk.size;
    return {track.track_id, video_id, track.time_scale, track.media_duration,
        track.sample_table.sample_count, static_cast<uint32_t>(track.sample_table.chunks.size()), payload_bytes};
}

bool mvhd_next_track_id(std::span<const uint8_t> data, size_t moov_start, size_t moov_end,
    isobmff_box_header& mvhd, size_t& next_id_offset, uint32_t& next_id) noexcept {
    size_t mvhd_start = 0;
    if (!unique_child(data, moov_start + 8U, moov_end, "mvhd", mvhd, mvhd_start) || mvhd.size < 20U) return false;
    const size_t body = mvhd_start + mvhd.header_size;
    const uint8_t version = data[body];
    if (version == 0U) next_id_offset = body + 96U;
    else if (version == 1U) next_id_offset = body + 108U;
    else return false;
    const size_t end = mvhd_start + mvhd.size;
    if (next_id_offset > end || end - next_id_offset < 4U) return false;
    next_id = read_u32(data.data() + next_id_offset);
    return next_id != 0U;
}

bool compare_payloads(std::span<const uint8_t> source, const track_record& source_track,
    std::span<const uint8_t> target, const track_record& target_track) noexcept {
    if (source_track.sample_table.chunks.size() != target_track.sample_table.chunks.size() ||
        source_track.sample_table.sample_count != target_track.sample_table.sample_count) return false;
    for (size_t index = 0; index < source_track.sample_table.chunks.size(); ++index) {
        const auto& original = source_track.sample_table.chunks[index];
        const auto& moved = target_track.sample_table.chunks[index];
        if (original.size != moved.size || original.sample_count != moved.sample_count ||
            original.first_sample != moved.first_sample || original.offset > source.size() ||
            original.size > source.size() - static_cast<size_t>(original.offset) || moved.offset > target.size() ||
            moved.size > target.size() - static_cast<size_t>(moved.offset) ||
            std::memcmp(source.data() + static_cast<size_t>(original.offset),
                target.data() + static_cast<size_t>(moved.offset), static_cast<size_t>(original.size)) != 0) return false;
    }
    return true;
}

} // namespace

bool inspect_isobmff_mebx_track(std::span<const uint8_t> data, bool& present,
    isobmff_mebx_track_facts& facts) noexcept {
    present = false;
    facts = {};
    try {
    file_record file{};
    if (!parse_file(data, file)) return false;
    track_record* mebx = nullptr;
    track_record* video = nullptr;
    if (!inspect_mebx(data, file, mebx, video)) return false;
    if (mebx == nullptr) return true;
    present = true;
    facts = facts_for(*mebx, video->track_id);
    return true;
    } catch (...) { present = false; facts = {}; return false; }
}

static bool validate_mebx_selected(std::span<const uint8_t> source,
    std::span<const uint8_t> output, const isobmff_mebx_track_facts& expected) noexcept;

static bool transplant_mebx_selected(std::span<const uint8_t> source,
    std::span<const uint8_t> stage_a, std::vector<uint8_t>& stage_b,
    uint32_t track_id, isobmff_mebx_track_facts& facts) noexcept {
    stage_b.clear();
    facts = {};
    try {
        file_record source_file{}, target_file{};
        if (!parse_file(source, source_file) || !parse_file(stage_a, target_file)) return false;
        const auto opaque=[](const auto& track){return !track.is_mebx&&std::memcmp(track.handler,"meta",4U)==0;};
        if(std::any_of(source_file.tracks.begin(),source_file.tracks.end(),opaque)||
            std::any_of(target_file.tracks.begin(),target_file.tracks.end(),opaque))return false;
        track_record* source_mebx = nullptr;
        track_record* source_video = nullptr;
        track_record* target_video = nullptr;
        std::vector<track_record*> source_tracks, target_tracks;
        if (!inspect_mebx_tracks(source, source_file, source_tracks, source_video) ||
            !inspect_mebx_tracks(stage_a, target_file, target_tracks, target_video) ||
            source_video == nullptr || target_video == nullptr) return false;
        for (auto* track : source_tracks) if (track->track_id == track_id) source_mebx = track;
        // A codec-transformed target does not inherit a source dependency by
        // copying its numeric ID. Only same-identity Copy remux supports cdep.
        if (source_mebx == nullptr || source_mebx->dependency_id_offset != 0U) return false;

        const uint32_t inserted_track_id = source_mebx->track_id;
        uint32_t maximum_track_id = 0U;
        for (const auto& track : target_file.tracks) {
            if (track.track_id == inserted_track_id || track.track_id > maximum_track_id) {
                if (track.track_id == inserted_track_id) return false;
                maximum_track_id = track.track_id;
            }
        }
        if (inserted_track_id == (std::numeric_limits<uint32_t>::max)()) return false;

        const size_t moov_size = target_file.moov_end - target_file.moov_start;
        const size_t track_size = source_mebx->end - source_mebx->start;
        if (source_mebx->start < source_file.moov_start || source_mebx->end > source_file.moov_end ||
            track_size > (std::numeric_limits<uint32_t>::max)() - 8U ||
            moov_size > (std::numeric_limits<uint32_t>::max)() - track_size ||
            target_file.moov_header_size != 8U || target_file.moov_end - target_file.moov_start > UINT32_MAX ||
            stage_a.size() > (std::numeric_limits<size_t>::max)() - track_size) return false;
        const size_t growth = track_size;
        const size_t expanded_moov_size = moov_size + growth;
        if (expanded_moov_size > UINT32_MAX || stage_a.size() > (std::numeric_limits<uint64_t>::max)() - growth) return false;

        isobmff_box_header mvhd{};
        size_t next_id_offset = 0;
        uint32_t current_next_id = 0;
        if (!mvhd_next_track_id(stage_a, target_file.moov_start, target_file.moov_end,
                mvhd, next_id_offset, current_next_id)) return false;
        const uint64_t required_next64 = std::max<uint64_t>(
            std::max<uint64_t>(static_cast<uint64_t>(maximum_track_id) + 1U,
                static_cast<uint64_t>(inserted_track_id) + 1U), current_next_id);
        if (required_next64 > UINT32_MAX) return false;

        std::vector<uint8_t> expanded_moov(
            stage_a.begin() + static_cast<ptrdiff_t>(target_file.moov_start),
            stage_a.begin() + static_cast<ptrdiff_t>(target_file.moov_end));
        if (expanded_moov.size() < 8U || read_u32(expanded_moov.data()) != expanded_moov.size()) return false;
        if (growth != 0U && !shift_chunk_offsets(expanded_moov, 0U,
                target_file.moov_end, static_cast<int64_t>(growth))) return false;
        write_u32(expanded_moov.data(), static_cast<uint32_t>(expanded_moov_size));
        const size_t target_mvhd_offset = next_id_offset - target_file.moov_start;
        if (target_mvhd_offset > expanded_moov.size() || expanded_moov.size() - target_mvhd_offset < 4U) return false;
        write_u32(expanded_moov.data() + target_mvhd_offset, static_cast<uint32_t>(required_next64));

        std::vector<uint8_t> copied_track(
            source.begin() + static_cast<ptrdiff_t>(source_mebx->start),
            source.begin() + static_cast<ptrdiff_t>(source_mebx->end));
        const size_t reference_offset = source_mebx->reference_id_offset - source_mebx->start;
        if (reference_offset > copied_track.size() || copied_track.size() - reference_offset < 4U ||
            !rebase_track_movie_durations(copied_track,
                source_file.movie_time_scale, target_file.movie_time_scale)) return false;
        write_u32(copied_track.data() + reference_offset, target_video->track_id);

        uint64_t payload_bytes = 0;
        for (const auto& chunk : source_mebx->sample_table.chunks) {
            if (payload_bytes > (std::numeric_limits<uint64_t>::max)() - chunk.size) return false;
            payload_bytes += chunk.size;
        }
        const uint64_t base_size = static_cast<uint64_t>(stage_a.size()) + growth;
        const uint64_t mdat_header_size = payload_bytes <= UINT32_MAX - 8U ? 8U : 16U;
        if (base_size > (std::numeric_limits<uint64_t>::max)() - mdat_header_size) return false;
        const uint64_t mdat_payload_start = base_size + mdat_header_size;
        uint64_t next_payload_offset = mdat_payload_start;
        for (size_t index = 0; index < source_mebx->sample_table.chunks.size(); ++index) {
            const auto& chunk = source_mebx->sample_table.chunks[index];
            const size_t field_absolute = source_mebx->sample_table.chunk_offsets_entries_start +
                index * source_mebx->sample_table.chunk_offset_width;
            if (field_absolute < source_mebx->start || field_absolute - source_mebx->start > copied_track.size() ||
                copied_track.size() - (field_absolute - source_mebx->start) < source_mebx->sample_table.chunk_offset_width) return false;
            const size_t field = field_absolute - source_mebx->start;
            if (source_mebx->sample_table.chunk_offset_width == 4U) {
                if (next_payload_offset > UINT32_MAX) return false;
                write_u32(copied_track.data() + field, static_cast<uint32_t>(next_payload_offset));
            } else if (source_mebx->sample_table.chunk_offset_width == 8U) {
                write_u64(copied_track.data() + field, next_payload_offset);
            } else {
                return false;
            }
            if (next_payload_offset > (std::numeric_limits<uint64_t>::max)() - chunk.size) return false;
            next_payload_offset += chunk.size;
        }
        if (next_payload_offset - mdat_payload_start != payload_bytes ||
            expanded_moov.size() > (std::numeric_limits<size_t>::max)() - copied_track.size()) return false;
        const size_t copied_track_start_in_moov = expanded_moov.size();
        expanded_moov.insert(expanded_moov.end(), copied_track.begin(), copied_track.end());

        const size_t shifted_mvhd_offset = target_mvhd_offset;
        if (shifted_mvhd_offset > expanded_moov.size() || expanded_moov.size() - shifted_mvhd_offset < 4U ||
            read_u32(expanded_moov.data() + shifted_mvhd_offset) != required_next64) return false;

        if (copied_track_start_in_moov != moov_size) return false;
        for (size_t index = 0; index < source_mebx->sample_table.chunks.size(); ++index) {
            const size_t field_absolute = source_mebx->sample_table.chunk_offsets_entries_start +
                index * source_mebx->sample_table.chunk_offset_width;
            const size_t field = copied_track_start_in_moov + (field_absolute - source_mebx->start);
            if (field > expanded_moov.size() || expanded_moov.size() - field < source_mebx->sample_table.chunk_offset_width) return false;
        }
        std::vector<uint8_t> payload;
        if (payload_bytes > (std::numeric_limits<size_t>::max)()) return false;
        payload.reserve(static_cast<size_t>(payload_bytes));
        for (const auto& chunk : source_mebx->sample_table.chunks) {
            if (chunk.offset > source.size() || chunk.size > source.size() - static_cast<size_t>(chunk.offset)) return false;
            payload.insert(payload.end(), source.begin() + static_cast<ptrdiff_t>(chunk.offset),
                source.begin() + static_cast<ptrdiff_t>(chunk.offset + chunk.size));
        }
        if (payload.size() != payload_bytes) return false;
        const uint64_t mdat_size = mdat_header_size + payload_bytes;
        std::vector<uint8_t> mdat;
        if (mdat_header_size == 8U) {
            mdat.resize(8U);
            write_u32(mdat.data(), static_cast<uint32_t>(mdat_size));
            std::memcpy(mdat.data() + 4U, "mdat", 4U);
        } else {
            mdat.resize(16U);
            write_u32(mdat.data(), 1U);
            std::memcpy(mdat.data() + 4U, "mdat", 4U);
            write_u64(mdat.data() + 8U, mdat_size);
        }
        if (payload.size() > (std::numeric_limits<size_t>::max)() - mdat.size()) return false;
        mdat.insert(mdat.end(), payload.begin(), payload.end());

        if (stage_a.size() > (std::numeric_limits<size_t>::max)() - growth ||
            stage_a.size() + growth > (std::numeric_limits<size_t>::max)() - mdat.size()) return false;
        stage_b.reserve(stage_a.size() + growth + mdat.size());
        size_t position = 0U;
        while (position < stage_a.size()) {
            isobmff_box_header box{};
            if (!try_read_box_header(stage_a.data(), position, stage_a.size(), box)) return false;
            if (position == target_file.moov_start) {
                stage_b.insert(stage_b.end(), expanded_moov.begin(), expanded_moov.end());
            } else {
                stage_b.insert(stage_b.end(), stage_a.begin() + static_cast<ptrdiff_t>(position),
                    stage_a.begin() + static_cast<ptrdiff_t>(position + box.size));
            }
            position += box.size;
        }
        stage_b.insert(stage_b.end(), mdat.begin(), mdat.end());
        file_record named_target{};
        if (!parse_file(stage_b, named_target) || !patch_handler_names(source, source_file, stage_b, named_target, true)) return false;
        const isobmff_mebx_track_facts expected_facts = facts_for(*source_mebx, target_video->track_id);
        if (!validate_mebx_selected(source, stage_b, expected_facts)) return false;
        file_record verified_target{};
        if (!parse_file(stage_b, verified_target)) return false;
        track_record* verified_target_mebx = nullptr;
        track_record* verified_target_video = nullptr;
        std::vector<track_record*> verified_tracks;
        if (!inspect_mebx_tracks(stage_b, verified_target, verified_tracks, verified_target_video)) return false;
        for (auto* track : verified_tracks) if (track->track_id == track_id) verified_target_mebx = track;
        if (
            verified_target_mebx == nullptr || verified_target_video == nullptr ||
            verified_target_mebx->start - verified_target.moov_start != copied_track_start_in_moov ||
            verified_target_mebx->end - verified_target_mebx->start != copied_track.size()) return false;
        facts = facts_for(*verified_target_mebx, verified_target_video->track_id);
        return true;
    } catch (...) {
        stage_b.clear();
        facts = {};
        return false;
    }
}

static bool validate_mebx_selected(std::span<const uint8_t> source,
    std::span<const uint8_t> output, const isobmff_mebx_track_facts& expected) noexcept {
    try {
        file_record source_file{}, output_file{};
        if (!parse_file(source, source_file) || !parse_file(output, output_file)) return false;
        const auto opaque=[](const auto& track){return !track.is_mebx&&std::memcmp(track.handler,"meta",4U)==0;};
        if(std::any_of(source_file.tracks.begin(),source_file.tracks.end(),opaque)||
            std::any_of(output_file.tracks.begin(),output_file.tracks.end(),opaque))return false;
        track_record* source_mebx = nullptr;
        track_record* source_video = nullptr;
        track_record* output_mebx = nullptr;
        track_record* output_video = nullptr;
        std::vector<track_record*> source_tracks, output_tracks;
        if (!inspect_mebx_tracks(source, source_file, source_tracks, source_video) ||
            !inspect_mebx_tracks(output, output_file, output_tracks, output_video)) return false;
        for (auto* track : source_tracks) if (track->track_id == expected.track_id) source_mebx = track;
        for (auto* track : output_tracks) if (track->track_id == expected.track_id) output_mebx = track;
        if (
            source_mebx == nullptr || output_mebx == nullptr || source_video == nullptr || output_video == nullptr ||
            source_mebx->dependency_id_offset != 0U || output_mebx->dependency_id_offset != 0U ||
            source_mebx->track_id != output_mebx->track_id ||
            source_mebx->time_scale != output_mebx->time_scale ||
            source_mebx->media_duration != output_mebx->media_duration ||
            source_mebx->sample_table.sample_count != output_mebx->sample_table.sample_count ||
            source_mebx->sample_table.chunk_offset_width != output_mebx->sample_table.chunk_offset_width ||
            source_mebx->sample_table.chunks.size() != output_mebx->sample_table.chunks.size() ||
            read_u32(output.data() + output_mebx->reference_id_offset) != output_video->track_id ||
            !compare_payloads(source, *source_mebx, output, *output_mebx)) return false;

        const auto actual = facts_for(*output_mebx, output_video->track_id);
        if (actual.track_id != expected.track_id ||
            actual.referenced_video_track_id != expected.referenced_video_track_id ||
            actual.time_scale != expected.time_scale || actual.media_duration != expected.media_duration ||
            actual.sample_count != expected.sample_count || actual.chunk_count != expected.chunk_count ||
            actual.payload_bytes != expected.payload_bytes) return false;

        std::vector<uint8_t> normalized_output_track(
            output.begin() + static_cast<ptrdiff_t>(output_mebx->start),
            output.begin() + static_cast<ptrdiff_t>(output_mebx->end));
        const size_t output_reference = output_mebx->reference_id_offset - output_mebx->start;
        const size_t source_reference = source_mebx->reference_id_offset - source_mebx->start;
        if (output_reference > normalized_output_track.size() ||
            normalized_output_track.size() - output_reference < 4U || source_reference != output_reference) return false;
        write_u32(normalized_output_track.data() + output_reference, source_video->track_id);
        for (size_t index = 0; index < source_mebx->sample_table.chunks.size(); ++index) {
            const size_t source_field = source_mebx->sample_table.chunk_offsets_entries_start +
                index * source_mebx->sample_table.chunk_offset_width;
            const size_t output_field = output_mebx->sample_table.chunk_offsets_entries_start +
                index * output_mebx->sample_table.chunk_offset_width;
            if (source_field < source_mebx->start || output_field < output_mebx->start ||
                source_field - source_mebx->start != output_field - output_mebx->start) return false;
            const size_t relative_field = output_field - output_mebx->start;
            const size_t width = output_mebx->sample_table.chunk_offset_width;
            if (relative_field > normalized_output_track.size() ||
                normalized_output_track.size() - relative_field < width) return false;
            std::memcpy(normalized_output_track.data() + relative_field,
                source.data() + source_field, width);
        }
        const size_t source_track_size = source_mebx->end - source_mebx->start;
        const size_t output_track_size = output_mebx->end - output_mebx->start;
        if (source_track_size != output_track_size ||
            !normalize_rebased_track_movie_durations(
                source.subspan(source_mebx->start, source_track_size),
                output.subspan(output_mebx->start, output_track_size),
                normalized_output_track, source_file.movie_time_scale, output_file.movie_time_scale)) return false;
        bool source_qt = false, output_qt = false;
        if (!quicktime_file(source, source_qt) || !quicktime_file(output, output_qt) ||
            !normalize_track_handler_names(output, *output_mebx, output_qt, source_qt, normalized_output_track)) return false;
        return normalized_output_track.size() == source_track_size &&
            std::memcmp(normalized_output_track.data(), source.data() + source_mebx->start,
                source_track_size) == 0;
    } catch (...) {
        return false;
    }
}

bool inspect_isobmff_mebx_tracks(std::span<const uint8_t> data,
    std::vector<isobmff_mebx_track_facts>& facts) noexcept {
    facts.clear();
    try {
        file_record file{};
        std::vector<track_record*> tracks;
        track_record* video = nullptr;
        if (!parse_file(data, file) || !inspect_mebx_tracks(data, file, tracks, video)) return false;
        for (const auto* track : tracks) facts.push_back(facts_for(*track, video->track_id));
        return true;
    } catch (...) { facts.clear(); return false; }
}

bool transplant_isobmff_mebx_track(std::span<const uint8_t> source,
    std::span<const uint8_t> stage_a, std::vector<uint8_t>& stage_b,
    isobmff_mebx_track_facts& facts) noexcept {
    stage_b.clear(); facts = {};
    bool source_present = false, target_present = false;
    isobmff_mebx_track_facts input{}, target{};
    if (!inspect_isobmff_mebx_track(source, source_present, input) || !source_present ||
        !inspect_isobmff_mebx_track(stage_a, target_present, target) || target_present) return false;
    return transplant_mebx_selected(source, stage_a, stage_b, input.track_id, facts);
}

bool validate_isobmff_mebx_transplant(std::span<const uint8_t> source,
    std::span<const uint8_t> output, const isobmff_mebx_track_facts& expected) noexcept {
    bool source_present = false, output_present = false;
    isobmff_mebx_track_facts input{}, actual{};
    return inspect_isobmff_mebx_track(source, source_present, input) && source_present &&
        inspect_isobmff_mebx_track(output, output_present, actual) && output_present &&
        validate_mebx_selected(source, output, expected);
}

bool validate_isobmff_mebx_transplants(std::span<const uint8_t> source,
    std::span<const uint8_t> output, std::span<const isobmff_mebx_track_facts> expected) noexcept {
    std::vector<isobmff_mebx_track_facts> original, actual;
    if (!inspect_isobmff_mebx_tracks(source, original) || !inspect_isobmff_mebx_tracks(output, actual) ||
        original.empty() || original.size() != actual.size() || original.size() != expected.size()) return false;
    for (size_t index = 0U; index < original.size(); ++index) {
        // Preserve ordered track identity as well as each complete track and payload.
        if (original[index].track_id != actual[index].track_id ||
            original[index].track_id != expected[index].track_id ||
            !validate_mebx_selected(source, output, expected[index])) return false;
    }
    return true;
}

bool transplant_isobmff_mebx_tracks(std::span<const uint8_t> source,
    std::span<const uint8_t> stage_a, std::vector<uint8_t>& stage_b,
    std::vector<isobmff_mebx_track_facts>& facts) noexcept {
    stage_b.clear(); facts.clear();
    try {
        std::vector<isobmff_mebx_track_facts> original, target;
        if (!inspect_isobmff_mebx_tracks(source, original) || original.empty() ||
            !inspect_isobmff_mebx_tracks(stage_a, target) || !target.empty()) return false;
        std::vector<uint8_t> current(stage_a.begin(), stage_a.end()), next;
        std::vector<isobmff_mebx_track_facts> moved_tracks;
        for (const auto& track : original) {
            isobmff_mebx_track_facts moved{};
            if (!transplant_mebx_selected(source, current, next, track.track_id, moved)) return false;
            moved_tracks.push_back(moved);
            current.swap(next);
        }
        if (!validate_isobmff_mebx_transplants(source, current, moved_tracks)) return false;
        facts.swap(moved_tracks);
        stage_b.swap(current);
        return true;
    } catch (...) { stage_b.clear(); facts.clear(); return false; }
}

namespace {
using static_boxes = std::vector<isobmff_box_header>;

bool children_of(std::span<const uint8_t> data, size_t begin, size_t end, static_boxes& boxes) {
    boxes.clear();
    while (begin < end) {
        isobmff_box_header box{};
        if (!try_read_box_header(data.data(), begin, end, box)) return false;
        boxes.push_back(box);
        begin += box.size;
    }
    return begin == end;
}

bool offset_free_meta(std::span<const uint8_t> data, const isobmff_box_header& meta) {
    size_t begin = meta.start + meta.header_size;
    const size_t end = meta.start + meta.size;
    if (end - begin < 8U) return false;
    // ISO FullBox versus the documented QuickTime direct-child form.
    if (read_u32(data.data() + begin) == 0U) begin += 4U;
    static_boxes children;
    if (!children_of(data, begin, end, children)) return false;
    const isobmff_box_header *handler = nullptr, *keys = nullptr, *items = nullptr;
    for (const auto& box : children) {
        if (box_type(data, box.start, "hdlr") && handler == nullptr) handler = &box;
        else if (box_type(data, box.start, "keys") && keys == nullptr) keys = &box;
        else if (box_type(data, box.start, "ilst") && items == nullptr) items = &box;
        else if((box_type(data,box.start,"free")||box_type(data,box.start,"skip"))&&
            read_u32(data.data()+box.start)!=0U&&
            std::all_of(data.begin()+box.start+box.header_size,data.begin()+box.start+box.size,
                [](uint8_t value){return value==0U;}))continue;
        else return false; // Includes iloc/iinf/external references and shadows.
    }
    if (handler == nullptr || items == nullptr || handler->size < handler->header_size + 24U) return false;
    const size_t hb = handler->start + handler->header_size;
    if (read_u32(data.data() + hb) != 0U) return false;
    const bool mdta = std::memcmp(data.data() + hb + 8U, "mdta", 4U) == 0;
    const bool mdir = std::memcmp(data.data() + hb + 8U, "mdir", 4U) == 0;
    if (!mdta && !mdir) return false;
    uint32_t key_count = 0U;
    if (mdta) {
        if (keys == nullptr || keys->size < keys->header_size + 8U) return false;
        size_t kb = keys->start + keys->header_size;
        if (read_u32(data.data() + kb) != 0U) return false;
        key_count = read_u32(data.data() + kb + 4U);
        kb += 8U;
        const size_t ke = keys->start + keys->size;
        if (key_count > (ke - kb) / 9U) return false;
        std::vector<std::string_view> names;
        for (uint32_t i = 0U; i < key_count; ++i) {
            if (ke - kb < 9U) return false;
            const size_t length = read_u32(data.data() + kb);
            if (length < 9U || length > ke - kb || std::memcmp(data.data() + kb + 4U, "mdta", 4U) != 0) return false;
            const std::string_view name(reinterpret_cast<const char*>(data.data() + kb + 8U), length - 8U);
            if (std::find(names.begin(), names.end(), name) != names.end()) return false;
            names.push_back(name);
            kb += length;
        }
        if (kb != ke) return false;
    } else if (keys != nullptr) return false;
    static_boxes entries;
    if (!children_of(data, items->start + items->header_size, items->start + items->size, entries)) return false;
    std::vector<uint32_t> identifiers;
    for (const auto& entry : entries) {
        const uint32_t id = read_u32(data.data() + entry.start + 4U);
        if (std::find(identifiers.begin(), identifiers.end(), id) != identifiers.end()) return false;
        identifiers.push_back(id);
        if (mdta && (id == 0U || id > key_count)) return false;
        if (mdir && (id >> 24U) != 0xA9U) return false;
        static_boxes values;
        if (!children_of(data, entry.start + entry.header_size, entry.start + entry.size, values) || values.empty()) return false;
        for (const auto& value : values) {
            if (!box_type(data, value.start, "data") || value.size < value.header_size + 8U) return false;
        }
    }
    return true;
}

bool copy_inline_user_data(std::span<const uint8_t> data,const isobmff_box_header& box) {
    if(read_u32(data.data()+box.start)==0U)return false;
    const size_t begin=box.start+box.header_size,end=box.start+box.size;
    if(box_type(data,box.start,"SDLN"))
        return end-begin==8U&&std::memcmp(data.data()+begin,"SEQ_PLAY",8U)==0;
    if(box_type(data,box.start,"smrd"))
        return end-begin==8U&&std::memcmp(data.data()+begin,"TRUEBLUE",8U)==0;
    if(!box_type(data,box.start,"smta")||end-begin<12U||read_u32(data.data()+begin)!=0U)return false;
    static_boxes fields;
    if(!children_of(data,begin+4U,end,fields)||fields.size()!=1U)return false;
    const auto& model=fields.front();
    if(!box_type(data,model.start,"mdln")||read_u32(data.data()+model.start)==0U||
        model.size<=model.header_size||model.size-model.header_size>256U)return false;
    return std::all_of(data.begin()+model.start+model.header_size,data.begin()+model.start+model.size,
        [](uint8_t value){return value>=0x20U&&value<=0x7eU;});
}

bool offset_free_static(std::span<const uint8_t> data, const isobmff_box_header& box,bool original_copy=false) {
    if(box_type(data,box.start,"gnre")) {
        return original_copy&&read_u32(data.data()+box.start)!=0U&&
            copy_inline_scene_metadata(data.subspan(box.start+box.header_size,box.size-box.header_size));
    }
    if (box_type(data, box.start, "lpex")) {
        // Observed OPPO inline extension with no matrix/reference payload.
        // Only this closed grammar is mapped, byte-for-byte; richer versions,
        // duplicate fields, unknown JSON or transformation matrices fail closed.
        const std::string_view text(reinterpret_cast<const char*>(data.data() + box.start + box.header_size),
            box.size - box.header_size);
        if(original_copy&&read_u32(data.data()+box.start)!=0U&&copy_inline_transform_metadata(text))return true;
        constexpr std::string_view prefix = "LivePhotoExtension{\"coverFramePts\":";
        constexpr std::string_view suffix = ",\"desc\":\"OppoMotionVideoExt\",\"matrixCount\":0,\"version\":0,\"videoOrientation\":0}";
        if (!text.starts_with(prefix) || !text.ends_with(suffix) || text.size() <= prefix.size() + suffix.size()) return false;
        const auto number = text.substr(prefix.size(), text.size() - prefix.size() - suffix.size());
        if (number.size() > 18U || (number.size() > 1U && number[0] == '0')) return false;
        return std::all_of(number.begin(), number.end(), [](char c) { return c >= '0' && c <= '9'; });
    }
    if (box_type(data, box.start, "meta")) return offset_free_meta(data, box);
    static_boxes children;
    if (!children_of(data, box.start + box.header_size, box.start + box.size, children)) return false;
    if (box_type(data, box.start, "tapt")) {
        if (children.size() != 3U) return false;
        const char* types[] = {"clef", "prof", "enof"};
        for (size_t i = 0U; i < 3U; ++i) {
            const auto& child = children[i];
            if (!box_type(data, child.start, types[i]) || child.size != child.header_size + 12U ||
                read_u32(data.data() + child.start + child.header_size) != 0U) return false;
        }
        return true;
    }
    if (!box_type(data, box.start, "udta")) return false;
    std::vector<uint32_t> types;
    for (const auto& child : children) {
        const uint32_t type = read_u32(data.data() + child.start + 4U);
        if (std::find(types.begin(), types.end(), type) != types.end()) return false;
        types.push_back(type);
        if (box_type(data, child.start, "meta")) {
            if (!offset_free_meta(data, child)) return false;
        } else if (box_type(data, child.start, "loci")) {
            // ISO location information: inline strings and fixed-point values,
            // without item/chunk/external references. MOV muxers can derive it
            // from a source QuickTime location tag.
            size_t p = child.start + child.header_size;
            const size_t end = child.start + child.size;
            if (end - p < 7U || read_u32(data.data() + p) != 0U) return false;
            const uint16_t language = static_cast<uint16_t>((data[p + 4U] << 8U) | data[p + 5U]);
            if ((language & 0x8000U) != 0U) return false;
            for (const unsigned shift : {10U, 5U, 0U}) {
                const unsigned letter = (language >> shift) & 31U;
                if (letter < 1U || letter > 26U) return false;
            }
            p += 6U; // FullBox and packed language.
            const auto string_end = [&]() {
                while (p < end && data[p] != 0U) ++p;
                if (p == end) return false;
                ++p; return true;
            };
            if (!string_end() || end - p < 13U || data[p] > 2U) return false;
            p += 13U; // Role, longitude, latitude, altitude.
            if (!string_end() || !string_end() || p != end) return false;
        } else if (box_type(data, child.start, "Xtra")) {
            // MF emits an empty Windows Media Xtra placeholder in udta.
            // A populated Xtra graph remains unsupported; do not infer that
            // arbitrary vendor payloads are offset-free from their box type.
            if (child.size != child.header_size) return false;
        } else if ((type >> 24U) != 0xA9U&&!(original_copy&&copy_inline_user_data(data,child))) return false;
    }
    return true;
}

bool same_metadata_key(std::span<const uint8_t> a, const isobmff_box_header& x,
    std::span<const uint8_t> b, const isobmff_box_header& y) {
    if (std::memcmp(a.data() + x.start + 4U, b.data() + y.start + 4U, 4U) != 0) return false;
    if (!box_type(a, x.start, "uuid")) return true;
    return x.size >= x.header_size + 16U && y.size >= y.header_size + 16U &&
        std::memcmp(a.data() + x.start + x.header_size, b.data() + y.start + y.header_size, 16U) == 0;
}

bool static_inventory(std::span<const uint8_t> data, size_t begin, size_t end,
    bool video, static_boxes& inventory, bool top = false, bool movie = false,bool original_copy=false) {
    static_boxes children;
    inventory.clear();
    if (!children_of(data, begin, end, children)) return false;
    for (const auto& box : children) {
        const bool uuid = box_type(data, box.start, "uuid");
        if (top && !uuid) {
            // These are regenerated/copied container scaffolding. Reference
            // indexes such as sidx/mfra cannot be silently omitted/rebased.
            if (!box_type(data, box.start, "ftyp") && !box_type(data, box.start, "moov") &&
                !box_type(data, box.start, "mdat") && !box_type(data, box.start, "free") &&
                !box_type(data, box.start, "skip") && !box_type(data, box.start, "wide")) return false;
            continue;
        }
        if (!uuid && !(movie && (box_type(data, box.start, "lpex")||box_type(data,box.start,"gnre"))) && !box_type(data, box.start, "meta") && !box_type(data, box.start, "udta") &&
            !(video && box_type(data, box.start, "tapt"))) {
            const bool scaffold = box_type(data, box.start, "free") || box_type(data, box.start, "skip") ||
                (movie ? box_type(data, box.start, "mvhd") || box_type(data, box.start, "trak") :
                    box_type(data, box.start, "tkhd") || box_type(data, box.start, "edts") ||
                    box_type(data, box.start, "mdia"));
            // A/V tref and unknown graph-bearing children require an explicit
            // relocation contract. Do not silently ignore them as scaffolding.
            if (!scaffold) return false;
            continue;
        }
        if (uuid ? box.size < box.header_size + 16U : !offset_free_static(data, box,original_copy)) return false;
        // An implicit-to-EOF box cannot be safely relocated/appended.
        if (read_u32(data.data() + box.start) == 0U) return false;
        for (const auto& prior : inventory) {
            if (same_metadata_key(data, prior, data, box)) return false;
        }
        inventory.push_back(box);
    }
    return true;
}

const track_record* unique_video(const file_record& file) {
    const track_record* result = nullptr;
    for (const auto& track : file.tracks) {
        if (std::memcmp(track.handler, "vide", 4U) != 0) continue;
        if (result != nullptr) return nullptr;
        result = &track;
    }
    return result;
}

bool hdr_static_inventory(std::span<const uint8_t> data, const track_record& video, static_boxes& found) {
    found.clear();
    isobmff_box_header stsd{};size_t stsd_start = 0U;
    if (!unique_child(data, video.stbl_start, video.stbl_end, "stsd", stsd, stsd_start) ||
        stsd.size < stsd.header_size + 8U) return false;
    const size_t body = stsd.start + stsd.header_size;
    if (read_u32(data.data() + body + 4U) != 1U) return false;
    isobmff_box_header entry{};
    if (!try_read_box_header(data.data(), body + 8U, stsd.start + stsd.size, entry) ||
        entry.start + entry.size != stsd.start + stsd.size || entry.size < entry.header_size + 78U) return false;
    size_t p = entry.start + entry.header_size + 78U;
    const size_t end = entry.start + entry.size;
    while (p < end) {
        if (end - p == 4U && read_u32(data.data() + p) == 0U) break;
        isobmff_box_header child{};
        if (!try_read_box_header(data.data(), p, end, child)) return false;
        const bool mastering = box_type(data, p, "mdcv"), light = box_type(data, p, "clli");
        const bool vivid = box_type(data, p, "cuvv");
        const bool aperture = box_type(data, p, "clap"), aspect = box_type(data, p, "pasp"), field = box_type(data, p, "fiel");
        if (mastering || light || vivid || aperture || aspect || field) {
            const size_t payload = mastering ? 24U : light ? 4U : vivid ? 22U : aperture ? 32U : aspect ? 8U : 2U;
            if (child.size != child.header_size + payload) return false;
            const uint8_t* values = data.data() + child.start + child.header_size;
            if (vivid) {
                // Frozen Huawei configuration observed in the actual HEVC
                // sample. It contains no location references. Unknown Vivid
                // configurations are not inferred from the box type alone.
                constexpr std::array<uint8_t, 22> configuration{0, 1, 0, 4, 0, 5};
                if ((!box_type(data, entry.start, "hvc1") && !box_type(data, entry.start, "hev1")) ||
                    std::memcmp(values, configuration.data(), configuration.size()) != 0) return false;
            }
            if (aperture && (read_u32(values) == 0U || read_u32(values + 4U) == 0U ||
                read_u32(values + 8U) == 0U || read_u32(values + 12U) == 0U ||
                read_u32(values + 20U) == 0U || read_u32(values + 28U) == 0U)) return false;
            if (aspect && (read_u32(values) == 0U || read_u32(values + 4U) == 0U)) return false;
            // Current codec paths encode progressive frames. Copying an
            // interlaced declaration onto them would be a semantic lie.
            if (field && (values[0] != 1U || values[1] != 0U)) return false;
            for (const auto& prior : found)
                if (std::memcmp(data.data() + prior.start + 4U, data.data() + p + 4U, 4U) == 0) return false;
            found.push_back(child);
        }
        p += child.size;
    }
    return true;
}

bool same_static(std::span<const uint8_t> source, const static_boxes& a,
    std::span<const uint8_t> target, const static_boxes& b) {
    for (const auto& box : a) {
        const isobmff_box_header* found = nullptr;
        for (const auto& candidate : b) {
            if (same_metadata_key(source, box, target, candidate)) found = &candidate;
        }
        if (found == nullptr || found->size != box.size ||
            std::memcmp(source.data() + box.start, target.data() + found->start, box.size) != 0) return false;
    }
    return true;
}

bool geometry_box(std::span<const uint8_t> data, size_t start) {
    return box_type(data, start, "clap") || box_type(data, start, "pasp") || box_type(data, start, "fiel") ||
        box_type(data, start, "cuvv");
}

bool replace_child_bytes(std::span<const uint8_t> data, const isobmff_box_header& parent,
    const isobmff_box_header& child, std::span<const uint8_t> replacement, std::vector<uint8_t>& result) {
    if (child.start < parent.start + parent.header_size || child.start + child.size > parent.start + parent.size) return false;
    result.assign(data.begin() + parent.start, data.begin() + child.start);
    result.insert(result.end(), replacement.begin(), replacement.end());
    result.insert(result.end(), data.begin() + child.start + child.size, data.begin() + parent.start + parent.size);
    if (parent.header_size == 16U) write_u64(result.data() + 8U, result.size());
    else {
        if (result.size() > UINT32_MAX) return false;
        write_u32(result.data(), static_cast<uint32_t>(result.size()));
    }
    return true;
}

bool preserve_visual_geometry(std::span<const uint8_t> source, const track_record& input,
    std::span<const uint8_t> stage, const track_record& target, std::vector<uint8_t>& result) {
    static_boxes metadata;
    if (!hdr_static_inventory(source, input, metadata)) return false;
    const auto visual_entry = [](std::span<const uint8_t> data, const track_record& track,
        isobmff_box_header& description, isobmff_box_header& entry) {
        size_t start = 0U;
        return unique_child(data, track.stbl_start, track.stbl_end, "stsd", description, start) &&
            description.size >= description.header_size + 8U &&
            read_u32(data.data() + description.start + description.header_size + 4U) == 1U &&
            try_read_box_header(data.data(), description.start + description.header_size + 8U,
                description.start + description.size, entry) && entry.size >= entry.header_size + 78U;
    };
    isobmff_box_header sd{}, se{}, td{}, te{};
    if (!visual_entry(source, input, sd, se) || !visual_entry(stage, target, td, te)) return false;
    // Exact coded dimensions (or transposed when rotated) are a prerequisite for copying source geometry.
    const uint16_t sw = (static_cast<uint16_t>(source[se.start + se.header_size + 24U]) << 8) | source[se.start + se.header_size + 25U];
    const uint16_t sh = (static_cast<uint16_t>(source[se.start + se.header_size + 26U]) << 8) | source[se.start + se.header_size + 27U];
    const uint16_t tw = (static_cast<uint16_t>(stage[te.start + te.header_size + 24U]) << 8) | stage[te.start + te.header_size + 25U];
    const uint16_t th = (static_cast<uint16_t>(stage[te.start + te.header_size + 26U]) << 8) | stage[te.start + te.header_size + 27U];
    if ((sw != tw || sh != th) && (sw != th || sh != tw)) return false;
    std::vector<uint8_t> entry(stage.begin() + te.start, stage.begin() + te.start + te.header_size + 78U);
    size_t p = te.start + te.header_size + 78U;
    const size_t end = te.start + te.size;
    while (p < end) {
        if (end - p == 4U && read_u32(stage.data() + p) == 0U) break;
        isobmff_box_header child{};
        if (!try_read_box_header(stage.data(), p, end, child)) return false;
        if (!geometry_box(stage, p)) entry.insert(entry.end(), stage.begin() + p, stage.begin() + p + child.size);
        p += child.size;
    }
    for (const auto& box : metadata) if (geometry_box(source, box.start))
        entry.insert(entry.end(), source.begin() + box.start, source.begin() + box.start + box.size);
    if (te.header_size == 16U) write_u64(entry.data() + 8U, entry.size());
    else { if (entry.size() > UINT32_MAX) return false; write_u32(entry.data(), static_cast<uint32_t>(entry.size())); }
    std::vector<uint8_t> child, parent;
    if (!replace_child_bytes(stage, td, te, entry, child)) return false;
    isobmff_box_header stbl{}, minf{}, mdia{}, trak{};
    size_t start = 0U;
    if (!try_read_box_header(stage.data(), target.start, target.end, trak) ||
        !unique_child(stage, target.start + trak.header_size, target.end, "mdia", mdia, start) ||
        !unique_child(stage, mdia.start + mdia.header_size, mdia.start + mdia.size, "minf", minf, start) ||
        !unique_child(stage, minf.start + minf.header_size, minf.start + minf.size, "stbl", stbl, start)) return false;
    if (!replace_child_bytes(stage, stbl, td, child, parent)) return false;
    child.swap(parent);
    if (!replace_child_bytes(stage, minf, stbl, child, parent)) return false;
    child.swap(parent);
    if (!replace_child_bytes(stage, mdia, minf, child, parent)) return false;
    child.swap(parent);
    return replace_child_bytes(stage, trak, mdia, child, result);
}

bool rewrite_static_parent(std::span<const uint8_t> source, const static_boxes& original,
    std::span<const uint8_t> target, size_t start, size_t end,
    const track_record* video, const std::vector<uint8_t>* rewritten_video,
    std::vector<uint8_t>& result, const track_record* audio = nullptr,
    const std::vector<uint8_t>* rewritten_audio = nullptr) {
    if (end - start < 8U || read_u32(target.data() + start) != end - start) return false;
    static_boxes children;
    if (!children_of(target, start + 8U, end, children)) return false;
    result.assign(target.begin() + start, target.begin() + start + 8U);
    std::vector<bool> used(original.size(), false);
    for (const auto& child : children) {
        if (audio != nullptr && child.start == audio->start) {
            result.insert(result.end(), rewritten_audio->begin(), rewritten_audio->end());
            continue;
        }
        if (video != nullptr && child.start == video->start) {
            result.insert(result.end(), rewritten_video->begin(), rewritten_video->end());
            continue;
        }
        size_t match = original.size();
        for (size_t i = 0U; i < original.size(); ++i) {
            if (same_metadata_key(source, original[i], target, child)) match = i;
        }
        if (match != original.size()) {
            if (used[match]) return false;
            used[match] = true;
            const auto& box = original[match];
            result.insert(result.end(), source.begin() + box.start, source.begin() + box.start + box.size);
        } else {
            const bool foreign_meta = box_type(target, child.start, "udta") ||
                box_type(target, child.start, "meta") ||
                box_type(target, child.start, "uuid") ||
                box_type(target, child.start, "lpex") ||
                box_type(target, child.start, "gnre");
            if (!foreign_meta) {
                result.insert(result.end(), target.begin() + child.start, target.begin() + child.start + child.size);
            }
        }
    }
    for (size_t i = 0U; i < original.size(); ++i) {
        if (!used[i]) {
            const auto& box = original[i];
            result.insert(result.end(), source.begin() + box.start, source.begin() + box.start + box.size);
        }
    }
    if (result.size() > UINT32_MAX) return false;
    write_u32(result.data(), static_cast<uint32_t>(result.size()));
    return true;
}
} // namespace

namespace {
const track_record* unique_audio(const file_record& file, bool& valid) {
    const track_record* result = nullptr;
    valid = true;
    for (const auto& track : file.tracks) {
        if (std::memcmp(track.handler, "soun", 4U) != 0 ||
            (track.media_duration==0U && track.stbl_start==track.stbl_end)) continue;
        if (result != nullptr) { valid = false; return nullptr; }
        result = &track;
    }
    return result;
}

bool classified_children(std::span<const uint8_t> data, size_t begin, size_t end,
    std::initializer_list<std::string_view> allowed, bool padding = false, bool copy_groups=false) {
    static_boxes children;
    size_t position = begin;
    while (position < end) {
        if (padding && end - position == 4U && read_u32(data.data() + position) == 0U) break;
        isobmff_box_header child{};
        if (!try_read_box_header(data.data(), position, end, child)) return false;
        children.push_back(child); position += child.size;
    }
    std::vector<uint32_t> seen;
    for (const auto& box : children) {
        const std::string_view type(reinterpret_cast<const char*>(data.data() + box.start + 4U), 4U);
        if (std::find(allowed.begin(), allowed.end(), type) == allowed.end()) return false;
        const uint32_t value = read_u32(data.data() + box.start + 4U);
        if (std::find(seen.begin(), seen.end(), value) != seen.end() &&
            !(copy_groups&&(type=="sgpd"||type=="sbgp"))) return false;
        seen.push_back(value);
    }
    return true;
}

bool explicit_copy_children(std::span<const uint8_t> data,size_t begin,size_t end,
    std::initializer_list<std::string_view> allowed) {
    if(!classified_children(data,begin,end,allowed))return false;
    static_boxes children;if(!children_of(data,begin,end,children))return false;
    return std::all_of(children.begin(),children.end(),[&](const auto& box){return read_u32(data.data()+box.start)!=0U;});
}

bool classified_edit_list(std::span<const uint8_t> data, const track_record& track) {
    isobmff_box_header edts{}, elst{};
    size_t p = 0U;
    if (!unique_child(data, track.start + 8U, track.end, "edts", edts, p, false)) return false;
    if (edts.size == 0U) return true;
    if (!classified_children(data, p + edts.header_size, p + edts.size, {"elst"}) ||
        !unique_child(data, p + edts.header_size, p + edts.size, "elst", elst, p) ||
        elst.size < elst.header_size + 8U) return false;
    const size_t body = p + elst.header_size;
    const uint32_t version_flags = read_u32(data.data() + body);
    if (version_flags != 0U && version_flags != 0x01000000U) return false;
    const size_t stride = version_flags == 0U ? 12U : 20U;
    const size_t count = read_u32(data.data() + body + 4U);
    if (count == 0U || count > (elst.size - elst.header_size - 8U) / stride ||
        elst.size != elst.header_size + 8U + count * stride) return false;
    for (size_t i = 0U; i < count; ++i) {
        const size_t rate = body + 8U + i * stride + stride - 4U;
        if (read_u32(data.data() + rate) != 0x00010000U) return false;
    }
    return true;
}

// Only understood codec-owned sync/AAC recovery groups may be regenerated.
// Private grouping types are metadata/reference graphs without a mapping.
bool classified_sample_groups(std::span<const uint8_t> data, const track_record& track) {
    isobmff_box_header sgpd{}, sbgp{}, cslg{};
    size_t gs = 0U, bs = 0U, cs = 0U;
    if (!unique_child(data, track.stbl_start, track.stbl_end, "sgpd", sgpd, gs, false) ||
        !unique_child(data, track.stbl_start, track.stbl_end, "sbgp", sbgp, bs, false) ||
        !unique_child(data, track.stbl_start, track.stbl_end, "cslg", cslg, cs, false)) return false;
    if ((sgpd.size == 0U) != (sbgp.size == 0U)) return false;
    if (sgpd.size != 0U) {
        isobmff_box_header stsd{}, codec{}, sizes{};
        size_t ss = 0U, zs = 0U;
        if (!unique_child(data, track.stbl_start, track.stbl_end, "stsd", stsd, ss) ||
            stsd.size < stsd.header_size + 8U ||
            !try_read_box_header(data.data(), ss + stsd.header_size + 8U, ss + stsd.size, codec)) return false;
        if (!unique_child(data, track.stbl_start, track.stbl_end, "stsz", sizes, zs, false)) return false;
        if (sizes.size == 0U && !unique_child(data, track.stbl_start, track.stbl_end, "stz2", sizes, zs)) return false;
        if (sizes.size < sizes.header_size + 12U) return false;
        const uint32_t sample_count = read_u32(data.data() + zs + sizes.header_size + 8U);
        const size_t g = gs + sgpd.header_size, b = bs + sbgp.header_size;
        if (sgpd.size < sgpd.header_size + 16U || sbgp.size < sbgp.header_size + 12U ||
            read_u32(data.data() + g) != 0x01000000U || read_u32(data.data() + b) != 0U ||
            std::memcmp(data.data() + g + 4U, data.data() + b + 4U, 4U) != 0) return false;
        const bool sync = std::memcmp(data.data() + g + 4U, "sync", 4U) == 0;
        const bool recovery = std::memcmp(data.data() + g + 4U, "roll", 4U) == 0 ||
            std::memcmp(data.data() + g + 4U, "prol", 4U) == 0;
        const size_t width = sync ? 1U : 2U;
        if ((sync && (std::memcmp(track.handler, "vide", 4U) != 0 ||
                (!box_type(data, codec.start, "hvc1") && !box_type(data, codec.start, "hev1")))) ||
            (recovery && (std::memcmp(track.handler, "soun", 4U) != 0 || !box_type(data, codec.start, "mp4a"))) ||
            (!sync && !recovery) || read_u32(data.data() + g + 8U) != width) return false;
        const size_t descriptions = read_u32(data.data() + g + 12U), runs = read_u32(data.data() + b + 8U);
        if (descriptions == 0U || descriptions > (sgpd.size - sgpd.header_size - 16U) / width ||
            sgpd.size != sgpd.header_size + 16U + descriptions * width ||
            runs > (sbgp.size - sbgp.header_size - 12U) / 8U || sbgp.size != sbgp.header_size + 12U + runs * 8U) return false;
        for (size_t i = 0U; i < descriptions; ++i) {
            const uint8_t* entry = data.data() + g + 16U + i * width;
            if (sync && entry[0] > 63U) return false;
            if (recovery && (entry[0] & 0x80U) == 0U && (entry[0] != 0U || entry[1] != 0U)) return false;
        }
        uint64_t samples = 0U;
        for (size_t i = 0U; i < runs; ++i) {
            const uint32_t count = read_u32(data.data() + b + 12U + i * 8U);
            if (count == 0U || read_u32(data.data() + b + 16U + i * 8U) > descriptions) return false;
            samples += count;
        }
        if (samples != sample_count) return false;
    }
    if (cslg.size != 0U) {
        const size_t body = cs + cslg.header_size;
        if (cslg.size < cslg.header_size + 4U) return false;
        const uint32_t version = read_u32(data.data() + body);
        if ((version != 0U || cslg.size != cslg.header_size + 24U) &&
            (version != 0x01000000U || cslg.size != cslg.header_size + 44U)) return false;
    }
    return true;
}

// Copy carries both HEVC temporal-layer descriptions ('tscl') and sync groups
// without regenerating them. These are inline codec descriptions/sample indices,
// not file addresses. Default transcode classification below remains unchanged.
bool classified_copy_groups(std::span<const uint8_t> data,const track_record& track,uint32_t sample_count) {
    static_boxes rows;if(!children_of(data,track.stbl_start,track.stbl_end,rows))return false;
    struct group {uint32_t type{},count{};bool mapped{};};std::vector<group> groups;
    for(const auto& box:rows)if(box_type(data,box.start,"sgpd")) {
        const size_t p=box.start+box.header_size;
        if(box.size<box.header_size+16U||read_u32(data.data()+p)!=0x01000000U)return false;
        const uint32_t type=read_u32(data.data()+p+4U),width=read_u32(data.data()+p+8U),count=read_u32(data.data()+p+12U);
        const bool sync=std::memcmp(data.data()+p+4U,"sync",4U)==0;
        const bool temporal=std::memcmp(data.data()+p+4U,"tscl",4U)==0;
        const bool roll=std::memcmp(data.data()+p+4U,"roll",4U)==0||std::memcmp(data.data()+p+4U,"prol",4U)==0;
        if((!sync&&!temporal&&!roll)||!count||!width||count>(box.size-box.header_size-16U)/width||
            box.size!=box.header_size+16U+static_cast<size_t>(count)*width)return false;
        if(sync||temporal) {
            isobmff_box_header stsd{},codec{};size_t s=0;
            if((std::memcmp(track.handler,"vide",4U)!=0&&std::memcmp(track.handler,"auxv",4U)!=0)||
                !unique_child(data,track.stbl_start,track.stbl_end,"stsd",stsd,s)||
                !try_read_box_header(data.data(),s+stsd.header_size+8U,s+stsd.size,codec)||
                (!box_type(data,codec.start,"hvc1")&&!box_type(data,codec.start,"hev1"))||
                width!=(sync?1U:20U))return false;
            if(sync)for(size_t i=0;i<count;++i)if(data[p+16U+i]>63U)return false;
        } else {
            if(std::memcmp(track.handler,"soun",4U)!=0||width!=2U)return false;
            isobmff_box_header stsd{},codec{};size_t s=0;
            if(!unique_child(data,track.stbl_start,track.stbl_end,"stsd",stsd,s)||
                !try_read_box_header(data.data(),s+stsd.header_size+8U,s+stsd.size,codec)||
                !box_type(data,codec.start,"mp4a"))return false;
            for(size_t i=0;i<count;++i)if((data[p+16U+i*2U]&0x80U)==0U&&
                (data[p+16U+i*2U]!=0U||data[p+17U+i*2U]!=0U))return false;
        }
        if(std::any_of(groups.begin(),groups.end(),[&](const auto& g){return g.type==type;}))return false;
        groups.push_back({type,count,false});
    }
    for(const auto& box:rows)if(box_type(data,box.start,"sbgp")) {
        const size_t p=box.start+box.header_size;
        if(box.size<box.header_size+12U||read_u32(data.data()+p)!=0U)return false;
        const uint32_t type=read_u32(data.data()+p+4U),count=read_u32(data.data()+p+8U);
        auto found=std::find_if(groups.begin(),groups.end(),[&](const auto& g){return g.type==type;});
        if(found==groups.end()||found->mapped||count>(box.size-box.header_size-12U)/8U||
            box.size!=box.header_size+12U+static_cast<size_t>(count)*8U)return false;
        uint64_t covered=0;
        for(size_t i=0;i<count;++i) {
            const auto n=read_u32(data.data()+p+12U+i*8U),index=read_u32(data.data()+p+16U+i*8U);
            if(!n||index>found->count)return false;covered+=n;
        }
        if(covered!=sample_count)return false;found->mapped=true;
    }
    for(const auto& g:groups)if(!g.mapped&&g.type!=0x7473636cU)return false;
    isobmff_box_header cslg{};size_t p=0;
    if(!unique_child(data,track.stbl_start,track.stbl_end,"cslg",cslg,p,false))return false;
    if(cslg.size) {
        if(cslg.size<cslg.header_size+4U)return false;
        const auto version=read_u32(data.data()+p+cslg.header_size);
        if((version!=0U||cslg.size!=cslg.header_size+24U)&&
            (version!=0x01000000U||cslg.size!=cslg.header_size+44U))return false;
    }
    return true;
}

bool classified_media_tree(std::span<const uint8_t> data, const track_record& track, bool video,
    std::span<const isobmff_media_data_range> mdats,bool copy=false,const char** reason=nullptr) {
    const auto fail=[&](const char* value){if(reason)*reason=value;return false;};
    isobmff_sample_table_layout layout{};
    if (!inspect_isobmff_sample_table(data, track.stbl_start, track.stbl_end, mdats, 0U, layout)) return fail("actual local sample table");
    if(!classified_edit_list(data,track))return fail("media edit list");
    if(!(copy?classified_copy_groups(data,track,layout.sample_count):classified_sample_groups(data,track)))return fail("codec sample group descriptions/mapping");
    isobmff_box_header mdia{}, minf{}, stsd{};
    size_t ms = 0U, ns = 0U, ss = 0U;
    if (!unique_child(data, track.start + 8U, track.end, "mdia", mdia, ms) || mdia.size == 0U ||
        !classified_children(data, ms + mdia.header_size, ms + mdia.size, {"mdhd", "hdlr", "minf"}) ||
        !unique_child(data, ms + mdia.header_size, ms + mdia.size, "minf", minf, ns) || minf.size == 0U ||
        !classified_children(data, ns + minf.header_size, ns + minf.size, video ?
            std::initializer_list<std::string_view>{"vmhd", "hdlr", "dinf", "stbl"} :
            std::initializer_list<std::string_view>{"smhd", "hdlr", "dinf", "stbl"})) return fail("media hierarchy children");
    size_t entry = 0U;
    if (!classified_children(data, track.stbl_start, track.stbl_end,
            {"stsd", "stts", "ctts", "stsc", "stsz", "stz2", "stco", "co64", "stss", "sdtp",
             "sbgp", "sgpd", "cslg"},false,copy) ||
        !unique_child(data, track.stbl_start, track.stbl_end, "stsd", stsd, ss) ||
        stsd.size < stsd.header_size + 8U) return fail("sample table child classification");
    entry = ss + stsd.header_size;
    if (read_u32(data.data() + entry) != 0U || read_u32(data.data() + entry + 4U) != 1U) return false;
    entry += 8U;
    isobmff_box_header codec{};
    if (!try_read_box_header(data.data(), entry, ss + stsd.size, codec) || entry + codec.size != ss + stsd.size) return false;
    const size_t body = entry + codec.header_size;
    if (!self_contained_reference(data, minf, codec)) return false;
    size_t prefix = 78U;
    if (!video) {
        if ((!box_type(data, entry, "mp4a") && !box_type(data, entry, "lpcm") &&
            !box_type(data, entry, "ipcm") && !box_type(data, entry, "sowt") && !box_type(data, entry, "twos")) ||
            codec.size < codec.header_size + 28U) return false;
        const uint16_t version = static_cast<uint16_t>((data[body + 8U] << 8U) | data[body + 9U]);
        prefix = version == 0U ? 28U : version == 1U ? 44U : version == 2U ? 64U : 0U;
        if (prefix == 0U) return false;
    }
    if (codec.size < codec.header_size + prefix) return false;
    const size_t end = entry + codec.size;
    if(copy&&video) {
        size_t p=body+prefix;
        while(p<end) {
            if(end-p==4U&&read_u32(data.data()+p)==0U)break;
            isobmff_box_header child{};if(!try_read_box_header(data.data(),p,end,child))return false;
            if(box_type(data,p,"logs")) {
                const std::string_view identifier(reinterpret_cast<const char*>(data.data()+p+child.header_size),
                    child.size-child.header_size);
                // Inline log transfer-function identifier, carried exactly;
                // no conversion or transfer-function interpretation is inferred.
                if(identifier!="com.apple.rec2020.apple-log"&&identifier!="com.apple.apple-wide-gamut.apple-log")return false;
            }
            p+=child.size;
        }
    }
    if (!video) {
        const uint32_t channels = prefix == 64U ? read_u32(data.data() + body + 40U) :
            static_cast<uint32_t>((data[body + 16U] << 8U) | data[body + 17U]);
        if (channels == 0U) return false;
        static_boxes extensions;
        if (!children_of(data, body + prefix, end, extensions)) return false;
        bool pcm_configuration = false;
        for (const auto& extension : extensions) {
            if (box_type(data, extension.start, "pcmC")) {
                if (!box_type(data, entry, "ipcm") || extension.size != extension.header_size + 6U) return false;
                const uint8_t* config = data.data() + extension.start + extension.header_size;
                if (read_u32(config) != 0U || config[4] > 1U || config[5] < 8U ||
                    config[5] > 32U || config[5] % 8U != 0U) return false;
                pcm_configuration = true;
            } else if (box_type(data, extension.start, "chan")) {
                if (extension.size != extension.header_size + 16U) return false;
                const uint8_t* channel_layout = data.data() + extension.start + extension.header_size;
                const uint32_t tag = read_u32(channel_layout + 4U);
                // Only the standard implicit mono/stereo layouts can be
                // regenerated from current codec channel facts. Descriptions,
                // bitmaps and richer/private layouts need explicit mapping.
                if (read_u32(channel_layout) != 0U || read_u32(channel_layout + 8U) != 0U || read_u32(channel_layout + 12U) != 0U ||
                    !((channels == 1U && tag == 0x00640001U) || (channels == 2U && tag == 0x00650002U))) return false;
            } else if (box_type(data, extension.start, "esds") && !box_type(data, entry, "mp4a")) return false;
        }
        if (box_type(data, entry, "ipcm") != pcm_configuration) return false;
    }
    const bool codec_children=classified_children(data, body + prefix, end, video ? (copy?
        std::initializer_list<std::string_view>{"avcC", "hvcC", "colr", "clap", "pasp", "btrt", "fiel", "mdcv", "clli", "cuvv", "logs"}:
        std::initializer_list<std::string_view>{"avcC", "hvcC", "colr", "clap", "pasp", "btrt", "fiel", "mdcv", "clli", "cuvv"}) :
        std::initializer_list<std::string_view>{"esds", "btrt", "pcmC", "chan"}, video);
    return codec_children?true:fail("codec extension children");
}

// Metadata projection for timed headers. Canonical v1 layout keeps every
// source field except the explicitly target-owned IDs/timebase/duration.
// The projection zeros only those mutable fields (and mvhd next-track-ID).
bool timed_header(std::span<const uint8_t> data, const isobmff_box_header& box,
    std::vector<uint8_t>& result, bool projection) {
    const bool movie = box_type(data, box.start, "mvhd"), track = box_type(data, box.start, "tkhd");
    if (!movie && !track && !box_type(data, box.start, "mdhd")) return false;
    const size_t body = box.start + box.header_size;
    if (box.size < box.header_size + 4U || data[body] > 1U) return false;
    const bool v1 = data[body] == 1U;
    const size_t prefix = track ? (v1 ? 36U : 24U) : (v1 ? 32U : 20U);
    const size_t tail = movie ? 80U : track ? 60U : 4U;
    if (box.size != box.header_size + prefix + tail) return false;
    // QuickTime uses these six ISO pre_defined words for movie-timescale
    // preview/poster/selection/current times. They cannot be copied across a
    // changed timebase. Until a time-field mapping exists, require all zero.
    if (movie && !std::all_of(data.begin() + body + prefix + 52U,
            data.begin() + body + prefix + 76U, [](uint8_t value) { return value == 0U; })) return false;
    if (!track && (read_u32(data.data() + body) & 0x00FFFFFFU) != 0U) return false;
    const uint64_t created = v1 ? read_u64(data.data() + body + 4U) : read_u32(data.data() + body + 4U);
    const uint64_t modified = v1 ? read_u64(data.data() + body + 12U) : read_u32(data.data() + body + 8U);
    const size_t dynamic = body + (v1 ? 20U : 12U);
    const uint32_t scale_or_id = read_u32(data.data() + dynamic);
    const size_t duration = dynamic + (track ? 8U : 4U);
    const uint64_t ticks = v1 ? read_u64(data.data() + duration) : read_u32(data.data() + duration);
    const size_t new_prefix = track ? 36U : 32U;
    result.assign(8U + new_prefix + tail, 0U);
    write_u32(result.data(), static_cast<uint32_t>(result.size()));
    std::memcpy(result.data() + 4U, data.data() + box.start + 4U, 4U);
    write_u32(result.data() + 8U, read_u32(data.data() + body) | 0x01000000U);
    write_u64(result.data() + 12U, created); write_u64(result.data() + 20U, modified);
    write_u32(result.data() + 28U, projection ? 0U : scale_or_id);
    if (track) std::memcpy(result.data() + 32U, data.data() + dynamic + 4U, 4U);
    write_u64(result.data() + (track ? 36U : 32U), projection ? 0U : ticks);
    std::memcpy(result.data() + 8U + new_prefix, data.data() + body + prefix, tail);
    if (movie && projection) write_u32(result.data() + result.size() - 4U, 0U);
    if (track && projection) {
        constexpr uint8_t be_identity[36] = {
            0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00
        };
        const bool rotated_matrix = std::memcmp(data.data() + body + prefix + 16U, be_identity, sizeof(be_identity)) != 0;
        std::memset(result.data() + 8U + new_prefix + 16U, 0, 36U);
        if (rotated_matrix) {
            const uint32_t w = read_u32(data.data() + body + prefix + 52U);
            const uint32_t h = read_u32(data.data() + body + prefix + 56U);
            write_u32(result.data() + 8U + new_prefix + 52U, std::min(w, h));
            write_u32(result.data() + 8U + new_prefix + 56U, std::max(w, h));
        }
    }
    return true;
}

bool mapped_timed_header(std::span<const uint8_t> source, const isobmff_box_header& original,
    std::span<const uint8_t> target, const isobmff_box_header& actual, std::vector<uint8_t>& result) {
    std::vector<uint8_t> current;
    if (!timed_header(source, original, result, false)) return false;
    if (!timed_header(target, actual, current, false)) return false;
    if (std::memcmp(result.data() + 4U, current.data() + 4U, 4U) != 0) return false;
    const bool track = box_type(source, original.start, "tkhd");
    std::memcpy(result.data() + 28U, current.data() + 28U, 4U); // target track-ID or timescale
    const size_t duration = track ? 36U : 32U;
    std::memcpy(result.data() + duration, current.data() + duration, 8U);
    if (box_type(source, original.start, "mvhd"))
        std::memcpy(result.data() + result.size() - 4U, current.data() + current.size() - 4U, 4U);
    if (track) {
        constexpr uint8_t be_identity[36] = {
            0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00
        };
        const bool src_rotated = std::memcmp(result.data() + 8U + 36U + 16U, be_identity, sizeof(be_identity)) != 0;
        if (src_rotated) {
            std::memcpy(result.data() + 8U + 36U + 16U, current.data() + 8U + 36U + 16U, 44U);
        }
    }
    return true;
}

bool valid_handler(std::span<const uint8_t> data, const isobmff_box_header& box) {
    return box.size >= box.header_size + 24U && read_u32(data.data() + box.start + box.header_size) == 0U;
}

bool map_header_parent(std::span<const uint8_t> source, const isobmff_box_header& original,
    std::span<const uint8_t> target, const isobmff_box_header& actual, std::vector<uint8_t>& result,
    const char** diag = nullptr) {
    const auto mfail = [&](const char* m) { if (diag) *diag = m; return false; };
    static_boxes before, after;
    if (!children_of(source, original.start + original.header_size, original.start + original.size, before) ||
        !children_of(target, actual.start + actual.header_size, actual.start + actual.size, after)) return mfail("children_of failed");
    result.assign(target.begin() + actual.start, target.begin() + actual.start + actual.header_size);
    bool copied_handler = false;
    for (const auto& child : after) {
        const bool timed = box_type(target, child.start, "mvhd") || box_type(target, child.start, "tkhd") || box_type(target, child.start, "mdhd");
        const bool handler = box_type(target, child.start, "hdlr");
        const bool ordinary = box_type(target,child.start,"elng") || box_type(target,child.start,"meta") || box_type(target,child.start,"udta") || box_type(target,child.start,"uuid");
        const bool container = box_type(target, child.start, "mdia") || box_type(target, child.start, "minf");
        if (!timed && !handler && !container && !ordinary) {
            result.insert(result.end(), target.begin() + child.start, target.begin() + child.start + child.size); continue;
        }
        const auto match = std::find_if(before.begin(), before.end(), [&](const auto& candidate) {
            return same_metadata_key(source, candidate, target, child);
        });
        if (match == before.end()) {
            if (ordinary) continue;
            if (!handler) {
                static char nomatch_msg[64]{};
                std::snprintf(nomatch_msg, sizeof(nomatch_msg), "no match in before for %.4s",
                    reinterpret_cast<const char*>(target.data() + child.start + 4U));
                return mfail(nomatch_msg);
            }
            result.insert(result.end(), target.begin() + child.start, target.begin() + child.start + child.size); continue;
        }
        std::vector<uint8_t> mapped;
        if (timed) { if (!mapped_timed_header(source, *match, target, child, mapped)) return mfail("mapped_timed_header failed"); }
        else if (handler) {
            if (!valid_handler(source, *match) || !valid_handler(target, child)) return mfail("valid_handler failed");
            mapped.assign(source.begin() + match->start, source.begin() + match->start + match->size); copied_handler = true;
        } else if(ordinary) {
            if(box_type(source,match->start,"elng")) {
                if(match->size<match->header_size+5U||read_u32(source.data()+match->start+match->header_size)!=0U||source[match->start+match->size-1U]!=0U)return false;
            } else if(!box_type(source,match->start,"uuid")&&!offset_free_static(source,*match,true))return false;
            mapped.assign(source.begin()+match->start,source.begin()+match->start+match->size);
        } else if (!map_header_parent(source, *match, target, child, mapped, diag)) return mfail("recursive map_header_parent");
        result.insert(result.end(), mapped.begin(), mapped.end());
    }
    // QuickTime minf may carry its own data-handler; ISO muxers omit it.
    if (!copied_handler) for (const auto& child : before) if (box_type(source, child.start, "hdlr")) {
        if (!valid_handler(source, child)) return mfail("valid_handler copied failed");
        result.insert(result.end(), source.begin() + child.start, source.begin() + child.start + child.size);
    }
    for(const auto& child:before) {
        const bool existed=std::any_of(after.begin(),after.end(),[&](const auto& c){return same_metadata_key(source,child,target,c);});
        if(existed||box_type(source,child.start,"hdlr"))continue;
        const bool language=box_type(source,child.start,"elng");
        const bool ordinary=language||box_type(source,child.start,"meta")||box_type(source,child.start,"udta")||box_type(source,child.start,"uuid");
        if(ordinary) {
            if(language) {
                if(child.size<child.header_size+5U||read_u32(source.data()+child.start+child.header_size)!=0U||source[child.start+child.size-1U]!=0U)return false;
            } else if(box_type(source,child.start,"uuid")?child.size<child.header_size+16U:!offset_free_static(source,child,true))return false;
            result.insert(result.end(),source.begin()+child.start,source.begin()+child.start+child.size);
        } else if(box_type(source,original.start,"mdia")||box_type(source,original.start,"minf")) {
            // Unknown source-only active structure cannot silently disappear.
            // Sample tables and mandatory media scaffolding must exist in both.
            if(!box_type(source,child.start,"free")&&!box_type(source,child.start,"skip"))return mfail("unknown source-only active structure");
        }
    }
    if (actual.header_size == 16U) write_u64(result.data() + 8U, result.size());
    else { if (result.size() > UINT32_MAX) return false; write_u32(result.data(), static_cast<uint32_t>(result.size())); }
    return true;
}

using header_projections = std::array<std::vector<uint8_t>, 9>;
bool capture_header_metadata(std::span<const uint8_t> data, const file_record& file, header_projections& result) {
    isobmff_box_header box{}; size_t position = 0U;
    bool quicktime = false;
    if (!quicktime_file(data, quicktime)) return false;
    if (!unique_child(data, file.moov_start + file.moov_header_size, file.moov_end, "mvhd", box, position) ||
        !timed_header(data, box, result[0], true)) return false;
    bool valid = false; const auto* audio = unique_audio(file, valid); const auto* video = unique_video(file);
    if (!valid || video == nullptr) return false;
    size_t index = 1U;
    for (const auto* track : {video, audio}) {
        if (track == nullptr) { index += 4U; continue; }
        isobmff_box_header mdia{}, minf{}, header{}; size_t p = 0U;
        if (!unique_child(data, track->start + 8U, track->end, "tkhd", header, p) || !timed_header(data, header, result[index], true) ||
            !unique_child(data, track->start + 8U, track->end, "mdia", mdia, p) ||
            !unique_child(data, mdia.start + mdia.header_size, mdia.start + mdia.size, "mdhd", header, p) ||
            !timed_header(data, header, result[index + 1U], true) ||
            !unique_child(data, mdia.start + mdia.header_size, mdia.start + mdia.size, "hdlr", header, p) || !valid_handler(data, header)) return false;
        if (!handler_name_bytes(data, header, quicktime, false, result[index + 2U])) return false;
        if (!unique_child(data, mdia.start + mdia.header_size, mdia.start + mdia.size, "minf", minf, p) ||
            !unique_child(data, minf.start + minf.header_size, minf.start + minf.size, "hdlr", header, p, false)) return false;
        if (header.size != 0U) {
            if (!valid_handler(data, header)) return false;
            if (!handler_name_bytes(data, header, quicktime, false, result[index + 3U])) return false;
        }
        index += 4U;
    }
    return true;
}

// Already recognized opaque timed syntax. Its bytes grant no target binding or
// cover authority. Only original Copy may carry it, with full stream proof.
bool copy_opaque_timed_track(std::span<const uint8_t> data,track_record& track,
    std::span<const isobmff_media_data_range> mdats) {
    if(track.is_mebx||std::memcmp(track.handler,"meta",4U)!=0||read_u32(data.data()+track.start)==0U||
        !explicit_copy_children(data,track.start+8U,track.end,{"tkhd","edts","mdia"})||
        !classified_edit_list(data,track))return false;
    isobmff_box_header edits{};size_t edit_at=0;
    if(!unique_child(data,track.start+8U,track.end,"edts",edits,edit_at,false)||
        (edits.size&&!explicit_copy_children(data,edit_at+edits.header_size,edit_at+edits.size,{"elst"})))return false;
    isobmff_box_header tkhd{},mdia{},mdhd{},handler{},minf{},nmhd{},stsd{};size_t k=0,m=0,d=0,h=0,n=0,u=0,s=0;
    if(!unique_child(data,track.start+8U,track.end,"tkhd",tkhd,k)||
        !unique_child(data,track.start+8U,track.end,"mdia",mdia,m)||
        !explicit_copy_children(data,m+mdia.header_size,m+mdia.size,{"mdhd","hdlr","minf"})||
        !unique_child(data,m+mdia.header_size,m+mdia.size,"mdhd",mdhd,d)||
        !unique_child(data,m+mdia.header_size,m+mdia.size,"hdlr",handler,h)||!valid_handler(data,handler)||
        !unique_child(data,m+mdia.header_size,m+mdia.size,"minf",minf,n)||
        !explicit_copy_children(data,n+minf.header_size,n+minf.size,{"nmhd","dinf","stbl"})||
        !unique_child(data,n+minf.header_size,n+minf.size,"nmhd",nmhd,u)||
        nmhd.size!=nmhd.header_size+4U||read_u32(data.data()+u+nmhd.header_size)||
        !local_data_reference(data,minf,true)||
        !explicit_copy_children(data,track.stbl_start,track.stbl_end,{"stsd","stts","ctts","stsc","stsz","stz2","stco","co64","stss","sdtp"})||
        !unique_child(data,track.stbl_start,track.stbl_end,"stsd",stsd,s)||stsd.size<stsd.header_size+8U)return false;
    const size_t sb=s+stsd.header_size,entry=sb+8U;
    isobmff_box_header mett{};constexpr char mime[]="application/livePhoto-meta";
    if(read_u32(data.data()+sb)||read_u32(data.data()+sb+4U)!=1U||
        !try_read_box_header(data.data(),entry,s+stsd.size,mett)||read_u32(data.data()+entry)==0U||
        !box_type(data,entry,"mett")||entry+mett.size!=s+stsd.size||mett.size-mett.header_size!=2U*sizeof(mime)||
        std::memcmp(data.data()+entry+mett.header_size,mime,sizeof(mime))||
        std::memcmp(data.data()+entry+mett.header_size+sizeof(mime),mime,sizeof(mime)))return false;
    if(tkhd.size<tkhd.header_size+4U||mdhd.size<mdhd.header_size+4U)return false;
    const size_t kb=k+tkhd.header_size,db=d+mdhd.header_size;
    const auto version=data[kb],media_version=data[db];
    if(version>1U||(read_u32(data.data()+kb)&0x00FFFFF0U)||tkhd.size-tkhd.header_size!=(version?96U:84U)||
        media_version>1U||(read_u32(data.data()+db)&0x00FFFFFFU)||mdhd.size-mdhd.header_size!=(media_version?36U:24U)||
        !read_media_timing(data,mdhd,track.time_scale,track.media_duration)||track.media_duration==0U)return false;
    const size_t matrix=kb+(version?52U:40U);
    constexpr uint32_t identity[]={0x00010000U,0,0,0,0x00010000U,0,0,0,0x40000000U,0,0};
    for(size_t i=0;i<std::size(identity);++i)if(read_u32(data.data()+matrix+i*4U)!=identity[i])return false;
    uint64_t bad=0;
    return inspect_isobmff_strict_sample_table(data,track.stbl_box_start,track.stbl_end,mdats,0U,track.sample_table,bad)&&
        track.sample_table.sample_count>0U;
}

bool scoped_inventory(std::span<const uint8_t> data, const file_record& file,
    std::array<static_boxes, 4>& scopes, bool auxiliary_copy=false,const char** reason=nullptr) {
    const auto* video = unique_video(file);
    bool valid = false;
    const auto* audio = unique_audio(file, valid);
    if (!valid || video == nullptr) return false;
    for (const auto& track : file.tracks) {
        if(auxiliary_copy&&!track.is_mebx&&std::memcmp(track.handler,"meta",4U)==0) {
            if(read_u32(data.data()+file.moov_start)==0U)return false;
            auto observed=track;if(!copy_opaque_timed_track(data,observed,file.mdats))return false;
            continue;
        }
        if (std::memcmp(track.handler, "vide", 4U) != 0 && std::memcmp(track.handler, "soun", 4U) != 0 &&
            !(track.is_mebx && std::memcmp(track.handler, "meta", 4U) == 0) &&
            !(auxiliary_copy && std::memcmp(track.handler,"auxv",4U)==0)) return false;
    }
    if (!classified_media_tree(data, *video, true, file.mdats,auxiliary_copy,reason) ||
        (audio != nullptr && !classified_media_tree(data, *audio, false, file.mdats,auxiliary_copy,reason))) return false;
    const auto check=[&](bool ok,const char* value){if(!ok&&reason)*reason=value;return ok;};
    return check(static_inventory(data, 0U, data.size(), false, scopes[0], true),"top static metadata") &&
        check(static_inventory(data, file.moov_start + file.moov_header_size, file.moov_end, false, scopes[1], false, true,auxiliary_copy),"movie static metadata") &&
        check(static_inventory(data, video->start + 8U, video->end, true, scopes[2]),"primary track static metadata") &&
        (audio == nullptr || check(static_inventory(data, audio->start + 8U, audio->end, false, scopes[3]),"audio static metadata"));
}

// Copy-only structural gate. Auxiliary bytes are NOT inserted into the public
// v1 digest scopes (0..13). Complete track/payload comparison below and the v2
// whole-file transport proof bind the additional media domain independently.
bool copy_additional_tracks(std::span<const uint8_t> data,file_record& file,
    std::vector<track_record*>& tracks) {
    tracks.clear();
    const auto* video=unique_video(file);if(!video)return false;
    for(auto& track:file.tracks) {
        if(!track.is_mebx&&std::memcmp(track.handler,"meta",4U)==0) {
            if(read_u32(data.data()+file.moov_start)==0U)return false;
            if(!copy_opaque_timed_track(data,track,file.mdats))return false;
            tracks.push_back(&track);continue;
        }
        if(std::memcmp(track.handler,"auxv",4U)!=0)continue;
        static_boxes geometry;
        if(!classified_media_tree(data,track,true,file.mdats,true)||!hdr_static_inventory(data,track,geometry))return false;
        isobmff_box_header tref{},vmap{},stsd{};size_t p=0,q=0,s=0;
        if(!unique_child(data,track.start+8U,track.end,"tref",tref,p)||
            !unique_child(data,p+tref.header_size,p+tref.size,"vmap",vmap,q)||
            vmap.size!=vmap.header_size+4U||tref.size!=tref.header_size+vmap.size||
            read_u32(data.data()+q+vmap.header_size)!=video->track_id||
            !unique_child(data,track.stbl_start,track.stbl_end,"stsd",stsd,s))return false;
        const size_t entry=s+stsd.header_size+8U;
        if(!box_type(data,entry,"hvc1")&&!box_type(data,entry,"hev1")&&
            !box_type(data,entry,"avc1")&&!box_type(data,entry,"avc3"))return false;
        isobmff_box_header visual{};
        if(!try_read_box_header(data.data(),entry,s+stsd.size,visual))return false;
        isobmff_video_profile codec;
        if(!inspect_isobmff_visual_codec_configuration(data.subspan(entry,visual.size),codec))return false;
        const size_t eb=entry+visual.header_size;
        const uint32_t width=(uint32_t(data[eb+24])<<8)|data[eb+25],height=(uint32_t(data[eb+26])<<8)|data[eb+27];
        if(!width||!height)return false;
        isobmff_box_header tkhd{},mdia{},mdhd{};size_t k=0,m=0,d=0;
        if(!unique_child(data,track.start+8U,track.end,"tkhd",tkhd,k)||tkhd.size<tkhd.header_size+4U||
            !unique_child(data,track.start+8U,track.end,"mdia",mdia,m)||
            !unique_child(data,m+mdia.header_size,m+mdia.size,"mdhd",mdhd,d)||mdhd.size<mdhd.header_size+4U)return false;
        const size_t kb=k+tkhd.header_size,db=d+mdhd.header_size;
        const auto version=data[kb];const auto media_version=data[db];
        if(version>1U||(read_u32(data.data()+kb)&0x00FFFFF0U)||
            tkhd.size-tkhd.header_size!=(version?96U:84U)||media_version>1U||
            (read_u32(data.data()+db)&0x00FFFFFFU)||mdhd.size-mdhd.header_size!=(media_version?36U:24U)||
            !read_media_timing(data,mdhd,track.time_scale,track.media_duration))return false;
        const size_t matrix=kb+(version?52U:40U);
        if(read_u32(data.data()+matrix+8U)||read_u32(data.data()+matrix+20U)||
            read_u32(data.data()+matrix+32U)!=0x40000000U||
            read_u32(data.data()+matrix+36U)!=(width<<16)||read_u32(data.data()+matrix+40U)!=(height<<16))return false;
        constexpr int32_t transforms[8][4]={{1,0,0,1},{-1,0,0,1},{-1,0,0,-1},{1,0,0,-1},{0,1,1,0},{0,1,-1,0},{0,-1,-1,0},{0,-1,1,0}};
        bool orientation=false;
        for(const auto& transform:transforms)
            if(read_u32(data.data()+matrix)==static_cast<uint32_t>(int64_t(transform[0])*65536)&&
                read_u32(data.data()+matrix+4U)==static_cast<uint32_t>(int64_t(transform[1])*65536)&&
                read_u32(data.data()+matrix+12U)==static_cast<uint32_t>(int64_t(transform[2])*65536)&&
                read_u32(data.data()+matrix+16U)==static_cast<uint32_t>(int64_t(transform[3])*65536))orientation=true;
        if(!orientation)return false;
        uint64_t bad=0;
        if(!inspect_isobmff_strict_sample_table(data,track.stbl_box_start,track.stbl_end,file.mdats,0U,track.sample_table,bad))return false;
        tracks.push_back(&track);
    }
    return true;
}
}

bool capture_isobmff_static_metadata_evidence(std::span<const uint8_t> source,
    std::span<const uint8_t> output, isobmff_static_metadata_evidence& evidence,
    std::span<const isobmff_media_data_range> source_mdats,
    std::span<const isobmff_media_data_range> output_mdats, const char** rejection_reason,
    bool auxiliary_copy) noexcept {
    evidence = {};
    const auto reason = [&](const char* value) { if (rejection_reason != nullptr) *rejection_reason = value; };
    reason("container hierarchy/sample tables");
    try {
        file_record input{}, target{};
        if (!parse_file(source, input, source_mdats) || !parse_file(output, target, output_mdats)) return false;
        reason("MEBX track hierarchy/references");
        std::vector<track_record*> source_mebx, output_mebx;
        track_record *source_video = nullptr, *output_video = nullptr;
        if (!inspect_mebx_tracks(source, input, source_mebx, source_video) ||
            !inspect_mebx_tracks(output, target, output_mebx, output_video)) return false;
        const auto* a = unique_video(input); const auto* b = unique_video(target);
        if (a == nullptr || b == nullptr) return false;
        bool av = false, bv = false;
        const auto* aa = unique_audio(input, av); const auto* ba = unique_audio(target, bv);
        if (!av || !bv || (aa == nullptr) != (ba == nullptr)) return false;
        std::array<static_boxes, 4> s{}, t{};
        static_boxes sh, th;
        reason("scoped static metadata/media-tree classification");
        if (!scoped_inventory(source, input, s,auxiliary_copy,rejection_reason) ||
            !scoped_inventory(output, target, t,auxiliary_copy,rejection_reason)) return false;
        if(auxiliary_copy) {
            std::vector<track_record*> sa,ta;
            if(!copy_additional_tracks(source,input,sa)||!copy_additional_tracks(output,target,ta)||sa.size()!=ta.size()||
                input.tracks.size()!=target.tracks.size()||!same_copy_track_graph(source,input,output,target))return false;
            for(size_t i=0;i<input.tracks.size();++i)
                if(input.tracks[i].track_id!=target.tracks[i].track_id||
                    std::memcmp(input.tracks[i].handler,target.tracks[i].handler,4U)!=0)return false;
        }
        reason("visual geometry/HDR descriptor classification");
        if (!hdr_static_inventory(source, *a, sh) || !hdr_static_inventory(output, *b, th) ||
            sh.size() != th.size() || !same_static(source, sh, output, th)) return false;
        reason("scoped metadata byte comparison");
        lpb::crypto::sha256_ctx source_hash, output_hash;
        for (uint8_t scope = 0U; scope < 5U; ++scope) {
            const auto& original = scope == 4U ? sh : s[scope];
            const auto& actual = scope == 4U ? th : t[scope];
            if (!same_static(source, original, output, actual)) return false;
            for (const auto& box : original) {
                const auto it = std::find_if(actual.begin(), actual.end(), [&](const auto& candidate) {
                    return same_metadata_key(source, box, output, candidate);
                });
                if (it == actual.end()) return false;
                source_hash.update(&scope, 1U); output_hash.update(&scope, 1U);
                source_hash.update(source.data() + box.start, box.size);
                output_hash.update(output.data() + it->start, it->size);
                ++evidence.box_count;
                if (box_type(source, box.start, "uuid")) ++evidence.opaque_count;
                if (scope == 3U) ++evidence.audio_count;
            }
        }
        reason("movie/track/media header metadata comparison");
        header_projections source_headers{}, output_headers{};
        if (!capture_header_metadata(source, input, source_headers) || !capture_header_metadata(output, target, output_headers)) return false;
        for (uint8_t index = 0U; index < source_headers.size(); ++index) {
            if (source_headers[index].empty()) continue;
            if (source_headers[index] != output_headers[index]) {
                static char hdr_err[256]{};
                uint32_t src_w = read_u32(source_headers[index].data() + 96U);
                uint32_t src_h = read_u32(source_headers[index].data() + 100U);
                uint32_t out_w = read_u32(output_headers[index].data() + 96U);
                uint32_t out_h = read_u32(output_headers[index].data() + 100U);
                std::snprintf(hdr_err, sizeof(hdr_err),
                    "idx %u: SRC(w=0x%08X, h=0x%08X) vs OUT(w=0x%08X, h=0x%08X)",
                    index, src_w, src_h, out_w, out_h);
                reason(hdr_err);
                return false;
            }
            const uint8_t scope = static_cast<uint8_t>(5U + index);
            source_hash.update(&scope, 1U); output_hash.update(&scope, 1U);
            source_hash.update(source_headers[index].data(), source_headers[index].size());
            output_hash.update(output_headers[index].data(), output_headers[index].size());
            ++evidence.box_count;
            if (index >= 5U) ++evidence.audio_count;
        }
        source_hash.finalize(evidence.source_sha256);
        output_hash.finalize(evidence.output_sha256);
        reason(nullptr);
        return std::memcmp(evidence.source_sha256, evidence.output_sha256, 32U) == 0;
    } catch (...) { evidence = {}; return false; }
}

bool validate_isobmff_mebx_stream_remux(std::span<const uint8_t> source_metadata,
    std::span<const isobmff_media_data_range> source_mdats, const lpb::random_access_reader& source,
    std::span<const uint8_t> output_metadata, std::span<const isobmff_media_data_range> output_mdats,
    const lpb::random_access_reader& output, bool (*cancelled)(void*), void* state) noexcept {
    try {
        isobmff_reader_observation strict_source{},strict_output{};
        isobmff_reader_diagnostic strict_error{};
        if((cancelled&&cancelled(state))||!inspect_isobmff_reader(source,true,strict_source,strict_error)||
            !inspect_isobmff_reader(output,true,strict_output,strict_error)||(cancelled&&cancelled(state)))return false;
        file_record a{}, b{};
        std::vector<track_record*> original, actual;
        track_record *av = nullptr, *bv = nullptr;
        if (!parse_file(source_metadata, a, source_mdats) || !parse_file(output_metadata, b, output_mdats) ||
            !inspect_mebx_tracks(source_metadata, a, original, av) ||
            !inspect_mebx_tracks(output_metadata, b, actual, bv) || original.size() != actual.size() ||
            a.movie_time_scale != b.movie_time_scale || a.movie_duration != b.movie_duration) return false;
        if(a.tracks.size()!=b.tracks.size()||!same_copy_track_graph(source_metadata,a,output_metadata,b))return false;
        for(size_t i=0;i<a.tracks.size();++i)
            if(a.tracks[i].track_id!=b.tracks[i].track_id||std::memcmp(a.tracks[i].handler,b.tracks[i].handler,4U)!=0)return false;
        std::vector<track_record*> source_aux,output_aux;
        if(!copy_additional_tracks(source_metadata,a,source_aux)||!copy_additional_tracks(output_metadata,b,output_aux)||
            source_aux.size()!=output_aux.size())return false;
        original.insert(original.end(),source_aux.begin(),source_aux.end());
        actual.insert(actual.end(),output_aux.begin(),output_aux.end());
        if (original.empty()) return true;
        const auto* primary_a=unique_video(a);const auto* primary_b=unique_video(b);
        if(primary_a==nullptr||primary_b==nullptr||primary_a->track_id!=primary_b->track_id)return false;
        bool source_qt = false, output_qt = false;
        if (!quicktime_file(source_metadata, source_qt) || !quicktime_file(output_metadata, output_qt)) return false;
        bool source_composition_qt=false,output_composition_qt=false;
        if(!quicktime_composition_file(source_metadata,source_composition_qt)||
            !quicktime_composition_file(output_metadata,output_composition_qt))return false;
        std::array<uint8_t, 64U * 1024U> x{}, y{};
        for (size_t i = 0U; i < original.size(); ++i) {
            const auto& s = *original[i]; const auto& t = *actual[i];
            if (s.track_id != t.track_id || s.end - s.start != t.end - t.start ||
                s.sample_table.chunk_offset_width != t.sample_table.chunk_offset_width ||
                s.sample_table.chunks.size() != t.sample_table.chunks.size()) return false;
            std::vector<uint8_t> normalized(output_metadata.begin() + t.start, output_metadata.begin() + t.end);
            std::vector<uint8_t> expected(source_metadata.begin()+s.start,source_metadata.begin()+s.end);
            for (size_t j = 0U; j < s.sample_table.chunks.size(); ++j) {
                const size_t sf = s.sample_table.chunk_offsets_entries_start + j * s.sample_table.chunk_offset_width;
                const size_t tf = t.sample_table.chunk_offsets_entries_start + j * t.sample_table.chunk_offset_width;
                const size_t width = t.sample_table.chunk_offset_width;
                if (sf < s.start || tf < t.start || sf - s.start != tf - t.start ||
                    tf - t.start > normalized.size() || width > normalized.size() - (tf - t.start)) return false;
                const auto& sc = s.sample_table.chunks[j]; const auto& tc = t.sample_table.chunks[j];
                if (sc.size != tc.size || sc.first_sample != tc.first_sample || sc.sample_count != tc.sample_count) return false;
                // Never erase an address merely because it falls in some mdat.
                // Both chunks must denote the same ordered mdat and exact
                // payload-relative offset before the address is normalized.
                const auto locate=[](const auto& ranges,const auto& chunk,size_t& owner,uint64_t& relative) {
                    size_t count=0;
                    for(size_t k=0;k<ranges.size();++k)if(chunk.offset>=ranges[k].start&&chunk.offset<ranges[k].end&&
                        chunk.size<=ranges[k].end-chunk.offset) {
                        ++count;owner=k;relative=chunk.offset-ranges[k].start;
                    }
                    return count==1;
                };
                size_t so=0,to=0;uint64_t sr=0,tr=0;
                if(!locate(a.mdats,sc,so,sr)||!locate(b.mdats,tc,to,tr)||so!=to||sr!=tr)return false;
                std::memcpy(normalized.data() + tf - t.start, source_metadata.data() + sf, width);
                uint64_t offset = 0U;
                while (offset < sc.size) {
                    if (cancelled != nullptr && cancelled(state)) return false;
                    const size_t count = static_cast<size_t>(std::min<uint64_t>(x.size(), sc.size - offset));
                    if (!source.read_exact(sc.offset + offset, std::span<uint8_t>(x).first(count)) ||
                        !output.read_exact(tc.offset + offset, std::span<uint8_t>(y).first(count)) ||
                        std::memcmp(x.data(), y.data(), count) != 0) return false;
                    offset += count;
                }
            }
            if (!normalize_track_handler_names(output_metadata, t, output_qt, source_qt, normalized) ||
                !map_remux_composition_versions(expected,source_composition_qt,false)||
                !map_remux_composition_versions(normalized,output_composition_qt,false)||normalized!=expected) return false;
        }
        return true;
    } catch (...) { return false; }
}

bool validate_isobmff_static_metadata(std::span<const uint8_t> source,
    std::span<const uint8_t> output) noexcept {
    isobmff_static_metadata_evidence evidence{};
    return capture_isobmff_static_metadata_evidence(source, output, evidence);
}

bool map_isobmff_handler_names(std::span<const uint8_t> source, std::span<const uint8_t> target,
    std::vector<uint8_t>& output, std::span<const isobmff_media_data_range> source_mdats,
    std::span<const isobmff_media_data_range> target_mdats) noexcept {
    output.clear();
    try {
        file_record before{}, after{};
        if (!parse_file(source, before, source_mdats) || !parse_file(target, after, target_mdats)) return false;
        output.assign(target.begin(), target.end());
        if (!patch_handler_names(source, before, output, after, false,true)) { output.clear(); return false; }
        return true;
    } catch (...) { output.clear(); return false; }
}

bool preserve_isobmff_copy_display_geometry(std::span<const uint8_t> source,
    std::span<const uint8_t> stage, std::vector<uint8_t>& output, bool& changed) noexcept {
    output.clear();changed=false;
    try {
        file_record input{},target{};
        if(!parse_file(source,input)||!parse_file(stage,target)||target.moov_header_size!=8U)return false;
        const auto* original=unique_video(input);const auto* actual=unique_video(target);
        if(!original||!actual)return false;
        std::vector<uint8_t> track,movie;
        if(!preserve_visual_geometry(source,*original,stage,*actual,track)||
            !rewrite_static_parent(stage,{},stage,target.moov_start,target.moov_end,actual,&track,movie))return false;
        const int64_t delta=int64_t(movie.size())-int64_t(target.moov_end-target.moov_start);
        if(delta&&!shift_chunk_offsets(movie,0U,target.moov_end,delta))return false;
        output.insert(output.end(),stage.begin(),stage.begin()+target.moov_start);
        output.insert(output.end(),movie.begin(),movie.end());
        output.insert(output.end(),stage.begin()+target.moov_end,stage.end());
        file_record final{};static_boxes before,after;
        if(!parse_file(output,final))return false;
        const auto* video=unique_video(final);
        if(!video||!hdr_static_inventory(source,*original,before)||!hdr_static_inventory(output,*video,after))return false;
        before.erase(std::remove_if(before.begin(),before.end(),[&](const auto& b){return !geometry_box(source,b.start);}),before.end());
        after.erase(std::remove_if(after.begin(),after.end(),[&](const auto& b){return !geometry_box(output,b.start);}),after.end());
        if(before.size()!=after.size()||!same_static(source,before,output,after))return false;
        changed=output.size()!=stage.size()||!std::equal(output.begin(),output.end(),stage.begin());return true;
    } catch(...){output.clear();changed=false;return false;}
}

bool preserve_isobmff_copy_timing(std::span<const uint8_t> source,
    std::span<const uint8_t> stage, std::vector<uint8_t>& output, bool& changed, const char** failure) noexcept {
    if(failure)*failure="container structure";
    output.clear(); changed = false;
    try {
        file_record input{}, target{};
        if (!parse_file(source, input) || !parse_file(stage, target) || target.moov_header_size != 8U) return false;
        const auto* original = unique_video(input);
        const auto* actual = unique_video(target);
        if (!original || !actual) return false;
        std::vector<uint8_t> track, movie;
        track.assign(stage.begin()+actual->start,stage.begin()+actual->end);
        isobmff_box_header src_edts{}, src_elst{};
        size_t src_edts_start = 0U, src_elst_start = 0U;
        if (!unique_child(source, original->start + 8U, original->end, "edts", src_edts, src_edts_start, false)) return false;
        const bool source_has_edts = src_edts.size > 0U;
        if (source_has_edts) {
            if (!unique_child(source, src_edts_start + src_edts.header_size, src_edts_start + src_edts.size, "elst", src_elst, src_elst_start, true) ||
                src_elst.size < src_elst.header_size + 8U) return false;
        }
        isobmff_box_header tgt_edts{};
        size_t tgt_edts_start = 0U;
        if (!unique_child(stage, actual->start + 8U, actual->end, "edts", tgt_edts, tgt_edts_start, false)) return false;
        const bool target_has_edts = tgt_edts.size > 0U;
        uint64_t edts_total_dur = 0U;
        bool edts_added = false;
        if (source_has_edts && !target_has_edts) {
            const size_t elst_body = src_elst_start + src_elst.header_size;
            const uint8_t src_ver = source[elst_body];
            if (src_ver > 1U) return false;
            if (source[elst_body + 1U] != 0U || source[elst_body + 2U] != 0U || source[elst_body + 3U] != 0U) return false;
            const size_t src_entry_size = src_ver == 0U ? 12U : 20U;
            const uint32_t count = read_u32(source.data() + elst_body + 4U);
            if (count != 1U && count != 2U) return false;
            if (src_elst.size != src_elst.header_size + 8U + static_cast<size_t>(count) * src_entry_size) return false;

            if (input.movie_time_scale == 0U || target.movie_time_scale == 0U ||
                original->time_scale == 0U || actual->time_scale == 0U) return false;

            if(failure)*failure="source/target raw presentation timelines";
            isobmff_video_presentation_timeline src_tl{}, tgt_tl{};
            if (!inspect_isobmff_video_presentation_timeline(source, src_tl) ||
                !inspect_isobmff_video_presentation_timeline(stage, tgt_tl) ||
                src_tl.presentation_order.empty() || tgt_tl.presentation_order.empty() ||
                src_tl.sample_count != tgt_tl.sample_count ||
                src_tl.presentation_order.size() != tgt_tl.presentation_order.size()) {
                return false;
            }

            const int64_t src_first_pts = src_tl.presentation_order.front().presentation_time_ticks;
            const int64_t tgt_first_pts = tgt_tl.presentation_order.front().presentation_time_ticks;
            int64_t src_first_pts_scaled = 0;
            if (!rescale_signed_time(src_first_pts, original->time_scale, actual->time_scale, src_first_pts_scaled)) return false;
            int64_t pts_shift = 0;
            if (!safe_sub_i64(tgt_first_pts, src_first_pts_scaled, pts_shift)) return false;

            if(failure)*failure="per-sample PTS correspondence";
            for (size_t s_idx = 0; s_idx < src_tl.presentation_order.size(); ++s_idx) {
                int64_t sample_pts_scaled = 0;
                if (!rescale_signed_time(src_tl.presentation_order[s_idx].presentation_time_ticks,
                        original->time_scale, actual->time_scale, sample_pts_scaled)) return false;
                int64_t expected_tgt_pts = 0;
                if (!safe_add_i64(sample_pts_scaled, pts_shift, expected_tgt_pts)) return false;
                const int64_t actual_tgt_pts = tgt_tl.presentation_order[s_idx].presentation_time_ticks;
                int64_t difference = 0;
                if (!safe_sub_i64(actual_tgt_pts, expected_tgt_pts, difference) || difference < -1 || difference > 1) return false;
                if (src_tl.presentation_order[s_idx].sample_index != tgt_tl.presentation_order[s_idx].sample_index) return false;
            }

            if(failure)*failure="edit duration/media-time translation";
            struct translated_edit {
                uint64_t dur{};
                int64_t media_time{};
                uint32_t media_rate{};
            };
            std::vector<translated_edit> entries(count);
            bool needs_v1 = (src_ver == 1U);

            for (uint32_t i = 0U; i < count; ++i) {
                const size_t entry_offset = elst_body + 8U + i * src_entry_size;
                uint64_t src_dur = 0U;
                int64_t src_media_time = 0;
                uint32_t media_rate = 0x00010000U;
                if (src_ver == 0U) {
                    src_dur = read_u32(source.data() + entry_offset);
                    src_media_time = static_cast<int32_t>(read_u32(source.data() + entry_offset + 4U));
                    media_rate = read_u32(source.data() + entry_offset + 8U);
                } else {
                    src_dur = read_u64(source.data() + entry_offset);
                    src_media_time = static_cast<int64_t>(read_u64(source.data() + entry_offset + 8U));
                    media_rate = read_u32(source.data() + entry_offset + 16U);
                }
                if (src_dur == 0U || src_media_time < -1) return false;
                if (media_rate != 0x00010000U) return false;

                if (count == 1U) {
                    if (src_media_time < 0) return false;
                } else {
                    if (i == 0U && src_media_time != -1) return false;
                    if (i == 1U && src_media_time < 0) return false;
                }

                uint64_t tgt_dur = 0U;
                if (!rescale_duration_ceil(src_dur, input.movie_time_scale, target.movie_time_scale, tgt_dur)) return false;

                int64_t tgt_media_time = -1;
                if (src_media_time >= 0) {
                    int64_t src_media_time_scaled = 0;
                    if (!rescale_signed_time(src_media_time, original->time_scale, actual->time_scale, src_media_time_scaled)) return false;
                    if (!safe_add_i64(src_media_time_scaled, pts_shift, tgt_media_time)) return false;
                    if (tgt_media_time < 0) return false;
                }

                if (tgt_dur > (std::numeric_limits<uint32_t>::max)() ||
                    tgt_media_time > (std::numeric_limits<int32_t>::max)() ||
                    tgt_media_time < (std::numeric_limits<int32_t>::min)()) {
                    needs_v1 = true;
                }

                if (!safe_add_u64(edts_total_dur, tgt_dur, edts_total_dur)) return false;

                entries[i] = {tgt_dur, tgt_media_time, media_rate};
            }

            const uint8_t tgt_ver = needs_v1 ? 1U : 0U;
            const size_t tgt_entry_size = tgt_ver == 0U ? 12U : 20U;
            std::vector<uint8_t> elst_bytes(8U + 4U + 4U + count * tgt_entry_size, 0U);
            write_u32(elst_bytes.data(), static_cast<uint32_t>(elst_bytes.size()));
            std::memcpy(elst_bytes.data() + 4U, "elst", 4U);
            elst_bytes[8U] = tgt_ver;
            write_u32(elst_bytes.data() + 12U, count);
            for (uint32_t i = 0U; i < count; ++i) {
                const size_t out_offset = 16U + i * tgt_entry_size;
                if (tgt_ver == 0U) {
                    write_u32(elst_bytes.data() + out_offset, static_cast<uint32_t>(entries[i].dur));
                    write_u32(elst_bytes.data() + out_offset + 4U, static_cast<uint32_t>(entries[i].media_time));
                    write_u32(elst_bytes.data() + out_offset + 8U, entries[i].media_rate);
                } else {
                    write_u64(elst_bytes.data() + out_offset, entries[i].dur);
                    write_u64(elst_bytes.data() + out_offset + 8U, static_cast<uint64_t>(entries[i].media_time));
                    write_u32(elst_bytes.data() + out_offset + 16U, entries[i].media_rate);
                }
            }
            std::vector<uint8_t> edts_bytes(8U + elst_bytes.size(), 0U);
            write_u32(edts_bytes.data(), static_cast<uint32_t>(edts_bytes.size()));
            std::memcpy(edts_bytes.data() + 4U, "edts", 4U);
            std::memcpy(edts_bytes.data() + 8U, elst_bytes.data(), elst_bytes.size());
            isobmff_box_header t_tkhd{};
            size_t t_tkhd_start = 0U;
            if (!unique_child(track, 8U, track.size(), "tkhd", t_tkhd, t_tkhd_start) ||
                t_tkhd.size < t_tkhd.header_size + 4U) return false;
            const size_t insert_pos = t_tkhd_start + t_tkhd.size;
            track.insert(track.begin() + insert_pos, edts_bytes.begin(), edts_bytes.end());
            write_u32(track.data(), static_cast<uint32_t>(track.size()));
            const uint8_t tkhd_ver = track[t_tkhd_start + t_tkhd.header_size];
            if (tkhd_ver > 1U) return false;
            const size_t dur_offset = t_tkhd_start + t_tkhd.header_size + (tkhd_ver == 0U ? 20U : 28U);
            if (dur_offset > t_tkhd_start + t_tkhd.size ||
                t_tkhd_start + t_tkhd.size - dur_offset < (tkhd_ver == 0U ? 4U : 8U)) return false;
            if (tkhd_ver == 0U) {
                if (edts_total_dur > (std::numeric_limits<uint32_t>::max)()) return false;
                write_u32(track.data() + dur_offset, static_cast<uint32_t>(edts_total_dur));
            } else {
                write_u64(track.data() + dur_offset, edts_total_dur);
            }
            edts_added = true;
        }
        if (!rewrite_static_parent(stage, {}, stage, target.moov_start, target.moov_end,
                actual, &track, movie)) return false;
        if (edts_added && edts_total_dur > 0U) {
            isobmff_box_header m_mvhd{};
            size_t m_mvhd_start = 0U;
            if (!unique_child(movie, 8U, movie.size(), "mvhd", m_mvhd, m_mvhd_start)) return false;
            const uint8_t mvhd_ver = movie[m_mvhd_start + m_mvhd.header_size];
            if (mvhd_ver > 1U) return false;

            uint64_t max_movie_dur = edts_total_dur;
            size_t child_pos = 8U;
            while (child_pos < movie.size()) {
                isobmff_box_header child_box{};
                if (!try_read_box_header(movie.data(), child_pos, movie.size(), child_box)) return false;
                if (box_type(movie, child_pos, "trak")) {
                    isobmff_box_header c_tkhd{};
                    size_t c_tkhd_start = 0U;
                    if (unique_child(movie, child_pos + child_box.header_size, child_pos + child_box.size, "tkhd", c_tkhd, c_tkhd_start)) {
                        const uint8_t c_tkhd_ver = movie[c_tkhd_start + c_tkhd.header_size];
                        if (c_tkhd_ver <= 1U) {
                            const size_t c_dur_offset = c_tkhd_start + c_tkhd.header_size + (c_tkhd_ver == 0U ? 20U : 28U);
                            const size_t req_size = c_tkhd.header_size + (c_tkhd_ver == 0U ? 24U : 36U);
                            if (c_tkhd.size >= req_size) {
                                const uint64_t trak_dur = (c_tkhd_ver == 0U)
                                    ? static_cast<uint64_t>(read_u32(movie.data() + c_dur_offset))
                                    : read_u64(movie.data() + c_dur_offset);
                                if (trak_dur > max_movie_dur) {
                                    max_movie_dur = trak_dur;
                                }
                            }
                        }
                    }
                }
                child_pos += child_box.size;
            }

            const size_t mvhd_dur_offset = m_mvhd_start + m_mvhd.header_size + (mvhd_ver == 0U ? 16U : 24U);
            if (mvhd_ver == 0U) {
                if (max_movie_dur > (std::numeric_limits<uint32_t>::max)()) return false;
                write_u32(movie.data() + mvhd_dur_offset, static_cast<uint32_t>(max_movie_dur));
            } else {
                write_u64(movie.data() + mvhd_dur_offset, max_movie_dur);
            }
        }
        const int64_t delta = static_cast<int64_t>(movie.size()) -
            static_cast<int64_t>(target.moov_end - target.moov_start);
        if (delta != 0 && !shift_chunk_offsets(movie, 0U, target.moov_end, delta)) return false;
        std::vector<uint8_t> result;
        result.insert(result.end(), stage.begin(), stage.begin() + target.moov_start);
        result.insert(result.end(), movie.begin(), movie.end());
        result.insert(result.end(), stage.begin() + target.moov_end, stage.end());
        file_record rebuilt{};
        if (!parse_file(result, rebuilt)) return false;
        const auto* final_video = unique_video(rebuilt);
        if(failure)*failure="final visible presentation timelines";
        isobmff_video_presentation_timeline before{}, after{};
        if (!final_video || !inspect_isobmff_video_presentation_timeline(source,before) ||
            !inspect_isobmff_video_presentation_timeline(result,after) ||
            before.visible_sample_count!=after.visible_sample_count ||
            before.presentation_order.size()!=after.presentation_order.size()) return false;
        if(failure)*failure="final per-frame visible interval equivalence";
        const int64_t tick_100ns = (10000000LL + after.media_timescale - 1U) / after.media_timescale;
        for(size_t i=0;i<before.presentation_order.size();++i) {
            const auto& a=before.presentation_order[i];const auto& b=after.presentation_order[i];
            if(a.sample_index!=b.sample_index||a.visible!=b.visible)return false;
            if(a.visible) {
                int64_t frame_delta=0;
                int64_t source_end=0,target_end=0;
                // Quantization applies independently to both endpoints. Check
                // those endpoints, not a duration difference that combines two
                // independent rounding errors.
                if(!safe_sub_i64(a.normalized_time_100ns,b.normalized_time_100ns,frame_delta) ||
                    frame_delta < -tick_100ns || frame_delta > tick_100ns ||
                    !safe_add_i64(a.normalized_time_100ns,a.duration_100ns,source_end) ||
                    !safe_add_i64(b.normalized_time_100ns,b.duration_100ns,target_end) ||
                    !safe_sub_i64(source_end,target_end,frame_delta) ||
                    frame_delta < -tick_100ns || frame_delta > tick_100ns)return false;
            }
        }
        changed = result.size() != stage.size() || !std::equal(result.begin(), result.end(), stage.begin());
        output.swap(result);
        return true;
    } catch (...) { output.clear(); changed = false; return false; }
}

// Read-only observation. Hash actual sample bytes in track/sample order;
// offsets, chunk grouping and timescales are never used as byte identity.
bool inspect_isobmff_asset_inventory(const lpb::random_access_reader& reader, std::string& json) noexcept {
    json.clear();
    try {
        std::vector<uint8_t> metadata; std::vector<isobmff_media_data_range> mdats;
        bool found=false,quicktime=false,found_ftyp=false; uint64_t end=0;
        const auto top=scan_top_level_boxes(reader);
        for(const auto& b:top) {
            if(b.offset!=end || b.size>reader.length-end)return false;
            end+=b.size;
            if(!std::memcmp(b.type,"ftyp",4U)) {
                if(found_ftyp||b.size>1024U*1024U)return false;
                std::vector<uint8_t> ftyp(size_t(b.size));
                if(!reader.read_exact(b.offset,ftyp)||!quicktime_file(ftyp,quicktime))return false;
                found_ftyp=true;
            }
            if(!std::memcmp(b.type,"moov",4U)) {
                if(found || b.size>64ULL*1024*1024)return false;
                found=true;metadata.resize(size_t(b.size));
                if(!reader.read_exact(b.offset,metadata))return false;
            } else if(!std::memcmp(b.type,"mdat",4U))mdats.push_back({b.offset+b.header_size,b.offset+b.size});
        }
        if(!found || end!=reader.length || mdats.empty())return false;
        // A bounded metadata-only container. All sample coordinates are pinned
        // to the real reader's mdat ranges, never to these synthetic offsets.
        const size_t moov_size=metadata.size();
        metadata.insert(metadata.end(),{0,0,0,8,'m','d','a','t'});
        file_record f{};
        if(!parse_file(metadata,f,mdats))return false;
        const auto hex=[](const uint8_t* bytes,size_t n){std::string r;r.reserve(n*2);for(size_t i=0;i<n;++i){r+="0123456789ABCDEF"[bytes[i]>>4];r+="0123456789ABCDEF"[bytes[i]&15];}return r;};
        const auto hash=[](std::span<const uint8_t> bytes){uint8_t h[32]{};lpb::crypto::sha256_buffer(bytes.data(),bytes.size(),h);std::string r;for(auto c:h){r+="0123456789ABCDEF"[c>>4];r+="0123456789ABCDEF"[c&15];}return r;};
        const auto quoted=[](std::string_view v){std::string r="\"";for(unsigned char c:v){if(c=='\"'||c=='\\'){r+='\\';r+=char(c);}else if(c>=32 && c<127)r+=char(c);else{r+="\\u00";r+="0123456789ABCDEF"[c>>4];r+="0123456789ABCDEF"[c&15];}}return r+"\"";};
        json="{\"schemaVersion\":1,\"assets\":[";bool comma=false;
        const auto add=[&](std::string item){if(comma)json+=",";comma=true;json+=item;};
        const auto header_asset=[&](const isobmff_box_header& b,const std::string& scope)->bool {
            std::vector<uint8_t> projection;
            if(box_type(metadata,b.start,"hdlr")) {
                if(!found_ftyp||!handler_name_bytes(metadata,b,quicktime,false,projection))return false;
            } else if(!timed_header(metadata,b,projection,true))return false;
            if(box_type(metadata,b.start,"tkhd")) {
                // Matrix and raster dimensions are observed by the separate
                // geometry/profile gate; ordinary header metadata is invariant.
                std::fill(projection.end()-44U,projection.end(),uint8_t{0});
            }
            add("{\"id\":"+quoted(scope+":header:"+hex(metadata.data()+b.start+4U,4U))+",\"kind\":\"Metadata\",\"byteLength\":"+std::to_string(projection.size())+",\"sha256\":\""+hash(projection)+"\"}");
            return true;
        };
        std::array<uint8_t,65536> buffer{};
        for(const auto& t:f.tracks) {
            if(t.stbl_start==t.stbl_end && t.media_duration==0U && std::memcmp(t.handler,"soun",4U)==0) {
                add("{\"id\":\"track:"+std::to_string(t.track_id)+"\",\"kind\":\"Track\",\"handler\":\"soun\",\"codec\":\"\",\"sampleCount\":0,\"byteLength\":0,\"sha256\":\""+hash({})+"\",\"descriptionSha256\":\"\"}");
            } else {
            isobmff_sample_table_layout table{};uint64_t bad=0;
            if(!inspect_isobmff_strict_sample_table(metadata,t.stbl_box_start,t.stbl_end,mdats,0U,table,bad))return false;
            lpb::crypto::sha256_ctx payload;uint64_t bytes=0;
            for(const auto& c:table.chunks) {
                if(!safe_add_u64(bytes,c.size,bytes))return false;
                for(uint64_t at=0;at<c.size;) {
                    const size_t n=size_t(std::min<uint64_t>(buffer.size(),c.size-at));
                    if(!reader.read_exact(c.offset+at,std::span<uint8_t>(buffer).first(n)))return false;
                    payload.update(buffer.data(),n);at+=n;
                }
            }
            uint8_t digest[32]{};payload.finalize(digest);
            isobmff_box_header sd{},entry{};size_t at=0;
            if(!unique_child(metadata,t.stbl_start,t.stbl_end,"stsd",sd,at)||
                !try_read_box_header(metadata.data(),at+sd.header_size+8U,at+sd.size,entry))return false;
            add("{\"id\":\"track:"+std::to_string(t.track_id)+"\",\"kind\":\"Track\",\"handler\":"+quoted(std::string_view(t.handler,4U))+
                ",\"codec\":"+quoted(std::string_view(reinterpret_cast<const char*>(metadata.data()+entry.start+4U),4U))+
                ",\"sampleCount\":"+std::to_string(table.sample_count)+",\"byteLength\":"+std::to_string(bytes)+
                ",\"sha256\":\""+hex(digest,32)+"\",\"descriptionSha256\":\""+hash(std::span<const uint8_t>(metadata).subspan(entry.start,entry.size))+"\"}");
            }
            const auto inventory=[&](auto&& self,size_t begin,size_t finish,const std::string& scope,unsigned depth)->bool {
                // Only the bounded track/mdia/minf ownership hierarchy is
                // observed. Hostile nested container chains must not exhaust
                // the process stack before normal format validation rejects them.
                if(depth>8U)return false;
                size_t p=begin;while(p<finish){isobmff_box_header b{};if(!try_read_box_header(metadata.data(),p,finish,b))return false;
                    if(box_type(metadata,p,"uuid")&&b.size<b.header_size+16U)return false;
                    if((box_type(metadata,p,"tkhd")||box_type(metadata,p,"mdhd")||box_type(metadata,p,"hdlr"))&&!header_asset(b,scope))return false;
                    if(box_type(metadata,p,"udta")||box_type(metadata,p,"meta")||box_type(metadata,p,"uuid")||box_type(metadata,p,"tapt")||box_type(metadata,p,"elng"))
                        add("{\"id\":"+quoted(scope+":"+hex(metadata.data()+p+4U,4U)+(box_type(metadata,p,"uuid")?":"+hex(metadata.data()+p+b.header_size,16U):""))+",\"kind\":"+quoted(box_type(metadata,p,"tapt")?"Geometry":"Metadata")+",\"byteLength\":"+std::to_string(b.size)+",\"sha256\":\""+hash(std::span<const uint8_t>(metadata).subspan(p,b.size))+"\"}");
                    if((box_type(metadata,p,"mdia")||box_type(metadata,p,"minf"))&&
                        !self(self,p+b.header_size,p+b.size,scope+":"+hex(metadata.data()+p+4U,4U),depth+1U))return false;
                    p+=b.size;}return true;};
            if(!inventory(inventory,t.start+8U,t.end,"track:"+std::to_string(t.track_id),0U))return false;
        }
        size_t p=f.moov_header_size;
        while(p<moov_size){isobmff_box_header b{};if(!try_read_box_header(metadata.data(),p,moov_size,b))return false;
            if(box_type(metadata,p,"uuid")&&b.size<b.header_size+16U)return false;
            if(box_type(metadata,p,"mvhd")&&!header_asset(b,"movie"))return false;
            if(box_type(metadata,p,"meta")||box_type(metadata,p,"udta")||box_type(metadata,p,"uuid"))
                add("{\"id\":\"movie:"+hex(metadata.data()+p+4U,4U)+(box_type(metadata,p,"uuid")?":"+hex(metadata.data()+p+b.header_size,16U):"")+"\",\"kind\":\"Metadata\",\"byteLength\":"+std::to_string(b.size)+",\"sha256\":\""+hash(std::span<const uint8_t>(metadata).subspan(p,b.size))+"\"}");
            p+=b.size;}
        for(const auto& top_box:top) {
            const bool uuid=std::memcmp(top_box.type,"uuid",4U)==0;
            if(!uuid&&std::memcmp(top_box.type,"meta",4U)&&std::memcmp(top_box.type,"udta",4U))continue;
            if(top_box.size>64ULL*1024*1024 || (uuid&&top_box.size<top_box.header_size+16U))return false;
            std::vector<uint8_t> bytes(size_t(top_box.size));if(!reader.read_exact(top_box.offset,bytes))return false;
            const std::string id=std::string("top:")+hex(reinterpret_cast<const uint8_t*>(top_box.type),4U)+(uuid?":"+hex(bytes.data()+top_box.header_size,16U):"");
            add("{\"id\":"+quoted(id)+",\"kind\":\"Metadata\",\"byteLength\":"+std::to_string(bytes.size())+",\"sha256\":\""+hash(bytes)+"\"}");
        }
        json+="]}";return true;
    } catch(...){json.clear();return false;}
}

bool isobmff_audio_tracks_requiring_aac(std::span<const uint8_t> source, std::vector<uint32_t>& ids) noexcept {
    ids.clear();
    try {
        file_record file{};if(!parse_file(source,file))return false;
        for(const auto& t:file.tracks)if(std::memcmp(t.handler,"soun",4U)==0) {
            if(t.stbl_start==t.stbl_end&&t.media_duration==0U)continue;
            isobmff_sample_table_layout table{};uint64_t bad=0;
            if(!inspect_isobmff_strict_sample_table(source,t.stbl_box_start,t.stbl_end,file.mdats,0U,table,bad))return false;
            if(!table.sample_count)continue;
            isobmff_box_header stsd{},entry{};size_t at=0;
            if(!unique_child(source,t.stbl_start,t.stbl_end,"stsd",stsd,at)||stsd.size<stsd.header_size+8U||
                read_u32(source.data()+at+stsd.header_size+4U)!=1U||
                !try_read_box_header(source.data(),at+stsd.header_size+8U,at+stsd.size,entry)||
                entry.start+entry.size!=at+stsd.size)return false;
            if(!box_type(source,entry.start,"mp4a"))ids.push_back(t.track_id);
        }
        return true;
    }catch(...){ids.clear();return false;}
}

bool extract_isobmff_audio_track(std::span<const uint8_t> source,uint32_t id,std::vector<uint8_t>& output) noexcept {
    output.clear();
    try {
        file_record file{};if(!parse_file(source,file)||file.moov_header_size!=8U)return false;
        const auto it=std::find_if(file.tracks.begin(),file.tracks.end(),[&](const auto& t){return id?t.track_id==id:std::memcmp(t.handler,"soun",4U)==0&&t.media_duration>0U;});
        if(it==file.tracks.end()||std::memcmp(it->handler,"soun",4U))return false;
        isobmff_box_header mv{};size_t m=0;
        if(!unique_child(source,file.moov_start+8U,file.moov_end,"mvhd",mv,m))return false;
        std::vector<uint8_t> movie(8U);std::memcpy(movie.data()+4U,"moov",4U);
        movie.insert(movie.end(),source.begin()+m,source.begin()+m+mv.size);
        movie.insert(movie.end(),source.begin()+it->start,source.begin()+it->end);
        if(movie.size()>UINT32_MAX)return false;write_u32(movie.data(),uint32_t(movie.size()));
        const int64_t growth=int64_t(movie.size())-int64_t(file.moov_end-file.moov_start);
        if(growth&&!shift_chunk_offsets(movie,0U,file.moov_end,growth))return false;
        output.insert(output.end(),source.begin(),source.begin()+file.moov_start);
        output.insert(output.end(),movie.begin(),movie.end());
        output.insert(output.end(),source.begin()+file.moov_end,source.end());
        file_record actual{};return parse_file(output,actual)&&actual.tracks.size()==1U&&actual.tracks[0].track_id==it->track_id;
    }catch(...){output.clear();return false;}
}

bool replace_isobmff_audio_track(std::span<const uint8_t> source,uint32_t id,
    std::span<const uint8_t> encoded,std::vector<uint8_t>& output) noexcept {
    output.clear();
    try {
        file_record sf{},ef{};
        if(!parse_file(source,sf)||!parse_file(encoded,ef)||sf.moov_header_size!=8U||ef.tracks.size()!=1U||
            std::memcmp(ef.tracks[0].handler,"soun",4U))return false;
        const auto original=std::find_if(sf.tracks.begin(),sf.tracks.end(),[&](const auto& t){return t.track_id==id;});
        if(original==sf.tracks.end()||std::memcmp(original->handler,"soun",4U))return false;
        const auto& audio=ef.tracks[0];
        isobmff_sample_table_layout table{};uint64_t bad=0;
        if(!inspect_isobmff_strict_sample_table(encoded,audio.stbl_box_start,audio.stbl_end,ef.mdats,0U,table,bad)||!table.sample_count)return false;
        std::vector<uint8_t> track(encoded.begin()+audio.start,encoded.begin()+audio.end);
        if(!rebase_track_movie_durations(track,ef.movie_time_scale,sf.movie_time_scale))return false;
        isobmff_box_header source_header{},current_header{};
        if(!try_read_box_header(source.data(),original->start,original->end,source_header)||
            !try_read_box_header(track.data(),0,track.size(),current_header))return false;
        std::vector<uint8_t> mapped;
        if(!map_header_parent(source,source_header,track,current_header,mapped))return false;
        bool source_qt=false,target_qt=false;
        if(!quicktime_file(source,source_qt)||!quicktime_file(encoded,target_qt))return false;
        if(!normalize_mapped_track_handler_names(source,*original,mapped,source_qt,target_qt))return false;
        track.swap(mapped);
        static_boxes source_children,ordinary;
        if(!children_of(source,original->start+8U,original->end,source_children))return false;
        for(const auto& child:source_children)if(box_type(source,child.start,"tref")||box_type(source,child.start,"udta")||box_type(source,child.start,"meta")||box_type(source,child.start,"uuid")) {
            if(!box_type(source,child.start,"tref")&&(box_type(source,child.start,"uuid")?child.size<child.header_size+16U:!offset_free_static(source,child,true)))return false;
            ordinary.push_back(child);
        }
        std::vector<uint8_t> with_metadata;
        if(!rewrite_static_parent(source,ordinary,track,0U,track.size(),nullptr,nullptr,with_metadata))return false;
        track.swap(with_metadata);
        isobmff_box_header tk{};size_t k=0;
        if(!unique_child(track,8U,track.size(),"tkhd",tk,k))return false;
        const size_t body=k+tk.header_size;
        write_u32(track.data()+body+(track[body]?20U:12U),id);
        std::vector<uint8_t> movie(source.begin()+sf.moov_start,source.begin()+sf.moov_end);
        isobmff_box_header old{original->start-sf.moov_start,original->end-original->start,8U};
        const int64_t growth=int64_t(track.size())-int64_t(old.size);
        // Relocate the unrelated source tables before inserting the replacement.
        // Encoded audio addresses are rewritten against its own appended mdat.
        if(growth&&!shift_chunk_offsets(movie,0U,sf.moov_end,growth))return false;
        std::vector<uint8_t> replaced;
        if(!replace_child_bytes(movie,isobmff_box_header{0U,movie.size(),8U},old,track,replaced))return false;
        movie.swap(replaced);
        // Locate by stable identity after header-size changes, without granting
        // temporary addresses authority over sample bytes.
        static_boxes children;if(!children_of(movie,8U,movie.size(),children))return false;
        size_t start=0,end=0;
        uint64_t duration=0;
        for(const auto& child:children)if(box_type(movie,child.start,"trak")) {
            isobmff_box_header h{};size_t at=0;uint32_t tid=0;
            if(!unique_child(movie,child.start+child.header_size,child.start+child.size,"tkhd",h,at)||!read_track_id(movie,h,tid))return false;
            const size_t b=at+h.header_size;const auto v=movie[b];const size_t d=b+(v?28U:20U);
            if(v>1U||d+(v?8U:4U)>at+h.size)return false;
            duration=std::max(duration,v?read_u64(movie.data()+d):uint64_t(read_u32(movie.data()+d)));
            if(tid==id){start=child.start;end=child.start+child.size;}
        }
        if(!end)return false;
        isobmff_box_header md{},mi{},st{},co{};size_t at=0;
        if(!unique_child(movie,start+8U,end,"mdia",md,at)||!unique_child(movie,md.start+md.header_size,md.start+md.size,"minf",mi,at)||
            !unique_child(movie,mi.start+mi.header_size,mi.start+mi.size,"stbl",st,at)||
            !unique_child(movie,st.start+st.header_size,st.start+st.size,"stco",co,at,false))return false;
        if(!co.size&&!unique_child(movie,st.start+st.header_size,st.start+st.size,"co64",co,at))return false;
        const size_t width=box_type(movie,co.start,"stco")?4U:8U;
        if(co.size!=co.header_size+8U+table.chunks.size()*width)return false;
        uint64_t payload_size=0;
        for(const auto& c:table.chunks)if(!safe_add_u64(payload_size,c.size,payload_size))return false;
        const uint64_t header=payload_size<=UINT32_MAX-8U?8U:16U;
        uint64_t payload_at=0;
        uint64_t total_without_audio=0;
        if(sf.moov_end-sf.moov_start>source.size()||
            !safe_add_u64(source.size()-(sf.moov_end-sf.moov_start),movie.size(),total_without_audio)||
            !safe_add_u64(total_without_audio,header,payload_at))return false;
        std::vector<uint8_t> payload;
        for(size_t i=0;i<table.chunks.size();++i) {
            const auto& c=table.chunks[i];const size_t field=at+co.header_size+8U+i*width;
            if(width==4U){if(payload_at>UINT32_MAX)return false;write_u32(movie.data()+field,uint32_t(payload_at));}
            else write_u64(movie.data()+field,payload_at);
            if(c.offset>encoded.size()||c.size>encoded.size()-c.offset||!safe_add_u64(payload_at,c.size,payload_at))return false;
            payload.insert(payload.end(),encoded.begin()+size_t(c.offset),encoded.begin()+size_t(c.offset+c.size));
        }
        isobmff_box_header mv{};size_t m=0;
        if(!unique_child(movie,8U,movie.size(),"mvhd",mv,m))return false;
        const size_t mb=m+mv.header_size;const auto version=movie[mb];
        if(version>1U||!write_movie_duration_field(movie.data()+mb+(version?24U:16U),version?8U:4U,duration))return false;
        output.insert(output.end(),source.begin(),source.begin()+sf.moov_start);
        output.insert(output.end(),movie.begin(),movie.end());output.insert(output.end(),source.begin()+sf.moov_end,source.end());
        const size_t tail=output.size();output.resize(tail+size_t(header));
        write_u32(output.data()+tail,header==8U?uint32_t(payload_size+8U):1U);std::memcpy(output.data()+tail+4U,"mdat",4U);
        if(header==16U)write_u64(output.data()+tail+8U,payload_size+16U);
        output.insert(output.end(),payload.begin(),payload.end());
        file_record final{};if(!parse_file(output,final)||final.tracks.size()!=sf.tracks.size())return false;
        isobmff_track_graph graph{};
        if(!inspect_isobmff_track_graph(std::span<const uint8_t>(output).subspan(final.moov_start,final.moov_end-final.moov_start),graph))return false;
        const auto replacement=std::find_if(final.tracks.begin(),final.tracks.end(),[&](const auto& t){return t.track_id==id;});
        if(replacement==final.tracks.end())return false;
        auto encoded_record=audio,actual_record=*replacement;encoded_record.sample_table=table;
        if(!inspect_isobmff_strict_sample_table(output,replacement->stbl_box_start,replacement->stbl_end,final.mdats,0U,actual_record.sample_table,bad)||
            !compare_payloads(encoded,encoded_record,output,actual_record))return false;
        for(const auto& t:sf.tracks)if(t.track_id!=id) {
            const auto f=std::find_if(final.tracks.begin(),final.tracks.end(),[&](const auto& x){return x.track_id==t.track_id;});
            if(f==final.tracks.end())return false;auto a=t,b=*f;
            if(!inspect_isobmff_strict_sample_table(source,t.stbl_box_start,t.stbl_end,sf.mdats,0U,a.sample_table,bad)||
                !inspect_isobmff_strict_sample_table(output,f->stbl_box_start,f->stbl_end,final.mdats,0U,b.sample_table,bad)||!compare_payloads(source,a,output,b))return false;
        }
        return true;
    }catch(...){output.clear();return false;}
}

// MF encodes only the audio. The existing video sample/timing tables are
// grafted onto that AAC movie without passing video through an MF reader/mux.
bool combine_isobmff_video_audio(std::span<const uint8_t> video,
    std::span<const uint8_t> audio, std::vector<uint8_t>& output) noexcept {
    output.clear();
    try {
        file_record vf{},af{};
        if(!parse_file(video,vf)||!parse_file(audio,af)||af.moov_header_size!=8U)return false;
        const auto* v=unique_video(vf);
        if(!v||unique_video(af)||af.tracks.size()!=1U||
            std::memcmp(af.tracks[0].handler,"soun",4U)!=0||af.tracks[0].track_id==UINT32_MAX)return false;
        auto copied=*v;uint64_t bad=0;
        if(!inspect_isobmff_strict_sample_table(video,v->stbl_box_start,v->stbl_end,vf.mdats,0U,copied.sample_table,bad))return false;
        std::vector<uint8_t> track(video.begin()+v->start,video.begin()+v->end);
        if(!rebase_track_movie_durations(track,vf.movie_time_scale,af.movie_time_scale))return false;
        isobmff_box_header tk{};size_t k=0;uint32_t ignored=0;
        if(!unique_child(track,8U,track.size(),"tkhd",tk,k)||!read_track_id(track,tk,ignored))return false;
        const size_t tb=k+tk.header_size;
        write_u32(track.data()+tb+(track[tb]?20U:12U),af.tracks[0].track_id+1U);
        bool source_qt=false,target_qt=false;
        if(!quicktime_file(video,source_qt)||!quicktime_file(audio,target_qt))return false;
        track_record local{};local.end=track.size();auto normalized=track;
        if(!normalize_track_handler_names(track,local,source_qt,target_qt,normalized))return false;
        track.swap(normalized);
        std::vector<uint8_t> movie(audio.begin()+af.moov_start,audio.begin()+af.moov_end);
        if(movie.size()>UINT32_MAX-track.size()||track.size()>INT64_MAX)return false;
        if(!shift_chunk_offsets(movie,0U,af.moov_end,int64_t(track.size())))return false;
        uint64_t payload_size=0;
        for(const auto& c:copied.sample_table.chunks)if(!safe_add_u64(payload_size,c.size,payload_size))return false;
        const uint64_t header=payload_size<=UINT32_MAX-8U?8U:16U;
        uint64_t payload_at=0;
        if(!safe_add_u64(audio.size(),track.size(),payload_at)||!safe_add_u64(payload_at,header,payload_at))return false;
        std::vector<uint8_t> payload;
        for(size_t i=0;i<copied.sample_table.chunks.size();++i) {
            const auto& c=copied.sample_table.chunks[i];
            const size_t field=copied.sample_table.chunk_offsets_entries_start+i*copied.sample_table.chunk_offset_width-v->start;
            if(field>track.size()||track.size()-field<copied.sample_table.chunk_offset_width||c.offset>video.size()||c.size>video.size()-c.offset)return false;
            if(copied.sample_table.chunk_offset_width==4U){if(payload_at>UINT32_MAX)return false;write_u32(track.data()+field,uint32_t(payload_at));}
            else write_u64(track.data()+field,payload_at);
            if(!safe_add_u64(payload_at,c.size,payload_at))return false;
            payload.insert(payload.end(),video.begin()+size_t(c.offset),video.begin()+size_t(c.offset+c.size));
        }
        const auto tk_version=track[tb];const size_t td=tb+(tk_version?28U:20U);
        if(tk_version>1U||td+(tk_version?8U:4U)>k+tk.size)return false;
        const uint64_t video_duration=tk_version?read_u64(track.data()+td):uint64_t(read_u32(track.data()+td));
        movie.insert(movie.end(),track.begin(),track.end());write_u32(movie.data(),uint32_t(movie.size()));
        if(!map_remux_composition_versions(movie,source_qt,target_qt))return false;
        isobmff_box_header mv{};size_t m=0;
        if(!unique_child(movie,8U,movie.size(),"mvhd",mv,m)||mv.size<mv.header_size+4U)return false;
        const size_t mb=m+mv.header_size;const auto version=movie[mb];
        const size_t dur=mb+(version?24U:16U),next=mb+(version?108U:96U);
        if(version>1U||next+4U>m+mv.size)return false;
        const auto duration=std::max(video_duration,af.movie_duration);
        if(!write_movie_duration_field(movie.data()+dur,version?8U:4U,duration)||af.tracks[0].track_id>=UINT32_MAX-1U)return false;
        write_u32(movie.data()+next,af.tracks[0].track_id+2U);
        output.insert(output.end(),audio.begin(),audio.begin()+af.moov_start);
        output.insert(output.end(),movie.begin(),movie.end());
        output.insert(output.end(),audio.begin()+af.moov_end,audio.end());
        const size_t at=output.size();output.resize(at+size_t(header));
        write_u32(output.data()+at,header==8U?uint32_t(payload_size+8U):1U);std::memcpy(output.data()+at+4U,"mdat",4U);
        if(header==16U)write_u64(output.data()+at+8U,payload_size+16U);
        output.insert(output.end(),payload.begin(),payload.end());
        file_record final{};
        if(!parse_file(output,final))return false;
        const auto* result=unique_video(final);if(!result)return false;
        auto observed=*result;
        if(!inspect_isobmff_strict_sample_table(output,result->stbl_box_start,result->stbl_end,final.mdats,0U,observed.sample_table,bad)||
            !compare_payloads(video,copied,output,observed))return false;
        return true;
    }catch(...){output.clear();return false;}
}

// Restore untouched sibling tracks after a main-video/audio transform. Source
// is the cleaned Neutral movie, never the original protocol-bearing movie.
bool preserve_isobmff_additional_tracks(std::span<const uint8_t> source,
    std::span<const uint8_t> stage, std::vector<uint8_t>& output, uint32_t rotation) noexcept {
    output.clear();
    try {
        file_record input{}, target{};
        if (!parse_file(source, input) || !parse_file(stage, target) || target.moov_header_size != 8U) return false;
        const auto* sv = unique_video(input); const auto* tv = unique_video(target);
        if (!sv || !tv || (rotation!=0U&&rotation!=90U&&rotation!=180U&&rotation!=270U)) return false;
        bool source_qt=false,target_qt=false;
        if(!quicktime_file(source,source_qt)||!quicktime_file(stage,target_qt))return false;
        const track_record *sa = nullptr, *ta = nullptr;
        for (const auto& t : input.tracks) if (std::memcmp(t.handler, "soun", 4U) == 0 && t.sample_table.sample_count > 0 && !sa) sa = &t;
        for (const auto& t : target.tracks) if (std::memcmp(t.handler, "soun", 4U) == 0 && t.sample_table.sample_count > 0 && !ta) ta = &t;
        if ((sa == nullptr) != (ta == nullptr)) return false;
        // Existing target must contain only the transformed main pair. Do not
        // infer how an unrelated target graph should be merged by numeric ID.
        if (target.tracks.size() != (ta ? 2U : 1U)) return false;
        std::vector<uint8_t> movie(stage.begin()+target.moov_start, stage.begin()+target.moov_end);
        const auto assign_id = [&](const track_record& t, uint32_t id) {
            isobmff_box_header h{}; size_t at=0;
            if (!unique_child(stage,t.start+8U,t.end,"tkhd",h,at)) return false;
            const size_t body=at+h.header_size;
            const size_t field=body+(stage[body]==0U?12U:20U);
            if (stage[body]>1U || field+4U>at+h.size) return false;
            write_u32(movie.data()+field-target.moov_start,id); return true;
        };
        if (!assign_id(*tv,sv->track_id) || (ta && !assign_id(*ta,sa->track_id))) return false;
        // Carry movie-level offset-free ordinary metadata. Its eligibility is
        // checked structurally; arbitrary unknown graphs are not relocated.
        static_boxes metadata;
        if (!static_inventory(source,input.moov_start+input.moov_header_size,input.moov_end,
                false,metadata,false,true,true)) return false;
        std::vector<uint8_t> mapped_movie;
        if (!rewrite_static_parent(source,metadata,movie,0U,movie.size(),nullptr,nullptr,mapped_movie)) return false;
        movie.swap(mapped_movie);
        isobmff_box_header source_mvhd{},target_mvhd{};size_t source_m=0,target_m=0;
        std::vector<uint8_t> mapped_mvhd,movie_with_header;
        if(!unique_child(source,input.moov_start+input.moov_header_size,input.moov_end,"mvhd",source_mvhd,source_m)||
            !unique_child(movie,8U,movie.size(),"mvhd",target_mvhd,target_m)||
            !mapped_timed_header(source,source_mvhd,movie,target_mvhd,mapped_mvhd)||
            !replace_child_bytes(movie,isobmff_box_header{0U,movie.size(),8U},target_mvhd,mapped_mvhd,movie_with_header))return false;
        movie.swap(movie_with_header);
        for (const auto* original : {sv,sa}) {
            if(!original)continue;
            static_boxes source_children,ordinary;
            if(!children_of(source,original->start+8U,original->end,source_children))return false;
            for(const auto& c:source_children) {
                if(box_type(source,c.start,"tref"))ordinary.push_back(c);
                else if(box_type(source,c.start,"udta")||box_type(source,c.start,"meta")||box_type(source,c.start,"uuid")||box_type(source,c.start,"tapt")) {
                    if(!box_type(source,c.start,"uuid")&&!offset_free_static(source,c,true))return false;
                    ordinary.push_back(c);
                }
            }
            static_boxes movie_children;if(!children_of(movie,8U,movie.size(),movie_children))return false;
            bool located=false;
            for(const auto& c:movie_children)if(box_type(movie,c.start,"trak")) {
                isobmff_box_header h{};size_t at=0;uint32_t id=0;
                if(!unique_child(movie,c.start+c.header_size,c.start+c.size,"tkhd",h,at)||!read_track_id(movie,h,id))return false;
                if(id!=original->track_id)continue;
                std::vector<uint8_t> merged_track,merged_movie,mapped_headers,current_tkhd;
                isobmff_box_header original_header{};
                if(!try_read_box_header(source.data(),original->start,original->end,original_header)||
                    !map_header_parent(source,original_header,movie,c,mapped_headers)||
                    !timed_header(movie,h,current_tkhd,false))return false;
                isobmff_box_header mapped_tkhd{};size_t mapped_at=0;
                if(!unique_child(mapped_headers,8U,mapped_headers.size(),"tkhd",mapped_tkhd,mapped_at)||mapped_tkhd.size<44U||current_tkhd.size()<44U)return false;
                // Keep target display matrix/dimensions after physical rotation;
                // creation dates, language, layer, alternate group and volume
                // remain source-owned ordinary header information.
                std::memcpy(mapped_headers.data()+mapped_at+mapped_tkhd.size-44U,current_tkhd.data()+current_tkhd.size()-44U,44U);
                if(!normalize_mapped_track_handler_names(source,*original,mapped_headers,source_qt,target_qt)||
                    !rewrite_static_parent(source,ordinary,mapped_headers,0U,mapped_headers.size(),nullptr,nullptr,merged_track))return false;
                if(original==sv&&(rotation==90U||rotation==270U)) {
                    isobmff_box_header aperture{};size_t ap=0;
                    if(!unique_child(merged_track,8U,merged_track.size(),"tapt",aperture,ap,false))return false;
                    if(aperture.size) {
                        static_boxes sizes;if(!children_of(merged_track,ap+aperture.header_size,ap+aperture.size,sizes))return false;
                        for(const auto& size:sizes) {
                            if(size.size!=size.header_size+12U)return false;
                            const size_t width=size.start+size.header_size+4U;
                            const auto w=read_u32(merged_track.data()+width),height=read_u32(merged_track.data()+width+4U);
                            write_u32(merged_track.data()+width,height);write_u32(merged_track.data()+width+4U,w);
                        }
                    }
                }
                if(!replace_child_bytes(movie,isobmff_box_header{0U,movie.size(),8U},c,merged_track,merged_movie))return false;
                movie.swap(merged_movie);located=true;break;
            }
            if(!located)return false;
        }
        std::vector<std::pair<const track_record*,std::vector<uint8_t>>> copied;
        uint64_t payload_size=0;
        uint32_t max_id=std::max(sv->track_id,sa?sa->track_id:0U);
        for (const auto& t : input.tracks) {
            if (&t==sv || &t==sa) continue;
            if (std::memcmp(t.handler,"vide",4U)==0) return false;
            if (std::memcmp(t.handler,"soun",4U)==0 && t.sample_table.sample_count==0) continue;
            if (t.is_mebx || std::memcmp(t.handler, "mdir", 4U) == 0) continue;
            if (std::memcmp(t.handler, "meta", 4U) == 0) {
                auto observed = t;
                if (!copy_opaque_timed_track(source, observed, input.mdats)) continue;
            }
            auto record=t; uint64_t bad=0;
            if (!inspect_isobmff_strict_sample_table(source,t.stbl_box_start,t.stbl_end,input.mdats,0U,record.sample_table,bad)) return false;
            std::vector<uint8_t> bytes(source.begin()+t.start,source.begin()+t.end);
            if (!rebase_track_movie_durations(bytes,input.movie_time_scale,target.movie_time_scale)) return false;
            track_record local{};local.end=bytes.size();auto normalized=bytes;
            if(!normalize_track_handler_names(bytes,local,source_qt,target_qt,normalized))return false;
            bytes.swap(normalized);
            for (const auto& c:record.sample_table.chunks)
                if (!safe_add_u64(payload_size,c.size,payload_size)) return false;
            max_id=std::max(max_id,t.track_id);
            copied.emplace_back(&t,std::move(bytes));
        }
        uint64_t expanded_size=movie.size();
        for(const auto& item:copied) if(!safe_add_u64(expanded_size,item.second.size(),expanded_size))return false;
        if (expanded_size>UINT32_MAX || max_id==UINT32_MAX) return false;
        const int64_t growth=int64_t(expanded_size)-int64_t(target.moov_end-target.moov_start);
        if (growth && !shift_chunk_offsets(movie,0U,target.moov_end,growth)) return false;
        uint64_t payload_at=0;
        const uint64_t header=payload_size<=UINT32_MAX-8U?8U:16U;
        if (growth < 0 && uint64_t(-growth)>stage.size()) return false;
        if(growth<0)payload_at=stage.size()-uint64_t(-growth);
        else if(!safe_add_u64(stage.size(),uint64_t(growth),payload_at))return false;
        if(!safe_add_u64(payload_at,header,payload_at))return false;
        std::vector<uint8_t> payload;
        if(payload_size>SIZE_MAX || payload_size>payload.max_size())return false;
        payload.reserve(size_t(payload_size));
        for(auto& [t,bytes]:copied) {
            isobmff_sample_table_layout table{}; uint64_t bad=0;
            if(!inspect_isobmff_strict_sample_table(source,t->stbl_box_start,t->stbl_end,input.mdats,0U,table,bad))return false;
            for(size_t i=0;i<table.chunks.size();++i) {
                const auto& c=table.chunks[i];
                const size_t field=table.chunk_offsets_entries_start+i*table.chunk_offset_width-t->start;
                if(field>bytes.size()||bytes.size()-field<table.chunk_offset_width)return false;
                if(table.chunk_offset_width==4U){if(payload_at>UINT32_MAX)return false;write_u32(bytes.data()+field,uint32_t(payload_at));}
                else write_u64(bytes.data()+field,payload_at);
                if(c.offset>source.size()||c.size>source.size()-c.offset||!safe_add_u64(payload_at,c.size,payload_at))return false;
                payload.insert(payload.end(),source.begin()+size_t(c.offset),source.begin()+size_t(c.offset+c.size));
            }
            movie.insert(movie.end(),bytes.begin(),bytes.end());
        }
        if(movie.size()>UINT32_MAX)return false;
        write_u32(movie.data(),uint32_t(movie.size()));
        isobmff_box_header mv{}; size_t m=0;
        if(!unique_child(movie,8U,movie.size(),"mvhd",mv,m)||mv.size<mv.header_size+4U)return false;
        const auto version=movie[m+mv.header_size];
        const size_t next=m+mv.header_size+(version==0U?96U:108U);
        if(version>1U||next+4U>m+mv.size)return false;
        write_u32(movie.data()+next,max_id+1U);
        // Movie duration is the maximum edited duration of every final track.
        uint64_t duration=0;
        static_boxes children;
        if(!children_of(movie,8U,movie.size(),children))return false;
        for(const auto& c:children)if(box_type(movie,c.start,"trak")) {
            isobmff_box_header h{};size_t k=0;
            if(!unique_child(movie,c.start+c.header_size,c.start+c.size,"tkhd",h,k)||h.size<h.header_size+4U)return false;
            const auto v=movie[k+h.header_size];const size_t f=k+h.header_size+(v?28U:20U);
            if(v>1U||f+(v?8U:4U)>k+h.size)return false;
            duration=std::max(duration,v?read_u64(movie.data()+f):uint64_t(read_u32(movie.data()+f)));
        }
        const size_t dur=m+mv.header_size+(version?24U:16U);
        if(!write_movie_duration_field(movie.data()+dur,version?8U:4U,duration))return false;
        output.insert(output.end(),stage.begin(),stage.begin()+target.moov_start);
        output.insert(output.end(),movie.begin(),movie.end());
        output.insert(output.end(),stage.begin()+target.moov_end,stage.end());
        if(!payload.empty()) {
            const size_t at=output.size();output.resize(at+size_t(header));
            write_u32(output.data()+at,header==8U?uint32_t(payload.size()+8U):1U);
            std::memcpy(output.data()+at+4U,"mdat",4U);
            if(header==16U)write_u64(output.data()+at+8U,payload.size()+16U);
            output.insert(output.end(),payload.begin(),payload.end());
        }
        // Carry already-cleaned top-level ordinary owners. No payload is
        // interpreted as a path or as further mutation authority.
        static_boxes top_source,top_target;
        if(!children_of(source,0U,source.size(),top_source)||!children_of(output,0U,output.size(),top_target))return false;
        for(const auto& owner:top_source) {
            if(!box_type(source,owner.start,"uuid")&&!box_type(source,owner.start,"meta")&&!box_type(source,owner.start,"udta"))continue;
            if((box_type(source,owner.start,"uuid")?owner.size<owner.header_size+16U:!offset_free_static(source,owner,true))||read_u32(source.data()+owner.start)==0U)return false;
            bool exists=false;
            for(const auto& current:top_target)if(same_metadata_key(source,owner,output,current)) {
                if(owner.size!=current.size||!std::equal(source.begin()+owner.start,source.begin()+owner.start+owner.size,output.begin()+current.start))return false;
                exists=true;
            }
            if(!exists)output.insert(output.end(),source.begin()+owner.start,source.begin()+owner.start+owner.size);
        }
        file_record final{};
        if(!parse_file(output,final)||final.tracks.size()!=input.tracks.size())return false;
        isobmff_track_graph graph{};
        if(!inspect_isobmff_track_graph(std::span<const uint8_t>(output).subspan(final.moov_start,final.moov_end-final.moov_start),graph))return false;
        for(const auto& t:input.tracks) {
            if(&t==sv||&t==sa)continue;
            const auto found=std::find_if(final.tracks.begin(),final.tracks.end(),[&](const auto& f){return f.track_id==t.track_id;});
            if(found==final.tracks.end())return false;
            auto before=t,after=*found;uint64_t bad=0;
            if(!inspect_isobmff_strict_sample_table(source,t.stbl_box_start,t.stbl_end,input.mdats,0U,before.sample_table,bad)||
                !inspect_isobmff_strict_sample_table(output,found->stbl_box_start,found->stbl_end,final.mdats,0U,after.sample_table,bad)||
                !compare_payloads(source,before,output,after))return false;
        }
        return true;
    } catch (...) {output.clear();return false;}
}

bool preserve_isobmff_static_metadata(std::span<const uint8_t> source,
    std::span<const uint8_t> stage, std::vector<uint8_t>& output, bool& changed,
    const char** reason) noexcept {
    output.clear(); changed = false;
    const auto fail = [&](const char* msg) { if (reason) *reason = msg; return false; };
    try {
        file_record input{}, target{};
        if (!parse_file(source, input)) return fail("parse_file source");
        if (!parse_file(stage, target)) return fail("parse_file stage");
        if (target.moov_header_size != 8U) return fail("stage moov_header_size != 8");
        const auto* a = unique_video(input); const auto* b = unique_video(target);
        bool av = false, bv = false;
        const auto* aa = unique_audio(input, av); const auto* ba = unique_audio(target, bv);
        if (!av) return fail("audio input invalid");
        if (!bv) return fail("audio stage invalid");
        if (a == nullptr) return fail("video input null");
        if (b == nullptr) return fail("video stage null");
        if ((aa == nullptr) != (ba == nullptr)) return fail("audio track presence mismatch");
        std::array<static_boxes, 4> s{}, t{};
        if (!scoped_inventory(source, input, s, false, reason)) return fail("scoped_inventory source");
        if (!scoped_inventory(stage, target, t, false, reason)) return fail("scoped_inventory stage");
        // Rebuild tracks first, then substitute both into one movie rewrite.
        std::vector<uint8_t> video, audio, movie, geometry_track, mapped;
        isobmff_box_header original{}, actual{};
        if (!preserve_visual_geometry(source, *a, stage, *b, geometry_track)) return fail("preserve_visual_geometry");
        if (!try_read_box_header(source.data(), a->start, a->end, original)) return fail("try_read_box_header a");
        if (!try_read_box_header(geometry_track.data(), 0U, geometry_track.size(), actual)) return fail("try_read_box_header geometry_track");
        if (!map_header_parent(source, original, geometry_track, actual, mapped)) return fail("map_header_parent video");
        if (!rewrite_static_parent(source, s[2], mapped, 0U, mapped.size(), nullptr, nullptr, video)) return fail("rewrite_static_parent video");
        if (aa != nullptr) {
            if (!try_read_box_header(source.data(), aa->start, aa->end, original)) return fail("try_read_box_header aa");
            if (!try_read_box_header(stage.data(), ba->start, ba->end, actual)) return fail("try_read_box_header ba");
            if (!map_header_parent(source, original, stage, actual, mapped)) return fail("map_header_parent audio");
            if (!rewrite_static_parent(source, s[3], mapped, 0U, mapped.size(), nullptr, nullptr, audio)) return fail("rewrite_static_parent audio");
        }
        if (!rewrite_static_parent(source, s[1], stage, target.moov_start, target.moov_end,
                b, &video, movie, ba, ba == nullptr ? nullptr : &audio)) return fail("rewrite_static_parent moov");
        if (!try_read_box_header(source.data(), input.moov_start, input.moov_end, original)) return fail("try_read_box_header input moov");
        if (!try_read_box_header(movie.data(), 0U, movie.size(), actual)) return fail("try_read_box_header actual moov");
        const char* mhp_err = nullptr; if (!map_header_parent(source, original, movie, actual, mapped, &mhp_err)) { if (reason) *reason = mhp_err ? mhp_err : "map_header_parent movie"; return false; }
        movie.swap(mapped);
        const int64_t delta = static_cast<int64_t>(movie.size()) - static_cast<int64_t>(target.moov_end - target.moov_start);
        if (delta != 0 && !shift_chunk_offsets(movie, 0U, target.moov_end, delta)) return fail("shift_chunk_offsets");
        std::vector<uint8_t> result;
        result.insert(result.end(), stage.begin(), stage.begin() + target.moov_start);
        result.insert(result.end(), movie.begin(), movie.end());
        result.insert(result.end(), stage.begin() + target.moov_end, stage.end());
        // Top UUIDs are appended after the existing file; never before mdat.
        for (const auto& box : s[0]) {
            const auto it = std::find_if(t[0].begin(), t[0].end(), [&](const auto& candidate) {
                return same_metadata_key(source, box, stage, candidate);
            });
            if (it != t[0].end()) {
                if (it->size != box.size || std::memcmp(stage.data() + it->start, source.data() + box.start, box.size) != 0) return fail("top uuid mismatch");
            } else result.insert(result.end(), source.begin() + box.start, source.begin() + box.start + box.size);
        }
        file_record named_target{};
        if (!parse_file(result, named_target)) return fail("parse_file result");
        if (!patch_handler_names(source, input, result, named_target, false)) return fail("patch_handler_names");
        const char* val_err = nullptr;
        isobmff_static_metadata_evidence ev{};
        if (!capture_isobmff_static_metadata_evidence(source, result, ev, {}, {}, &val_err)) {
            if (reason) *reason = val_err ? val_err : "validate_static_metadata";
            return false;
        }
        changed = result.size() != stage.size() || !std::equal(result.begin(), result.end(), stage.begin());
        output.swap(result);
        return true;
    } catch (...) { output.clear(); changed = false; return fail("exception"); }
}

} // namespace lpb::media
