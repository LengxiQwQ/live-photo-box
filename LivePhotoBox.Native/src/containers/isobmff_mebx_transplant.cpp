#include "containers/isobmff_mebx_transplant.h"

#include "containers/isobmff.h"

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
    size_t stbl_end{};
    size_t sample_entry_offset{};
    uint32_t track_id{};
    uint32_t time_scale{};
    uint64_t media_duration{};
    char handler[4]{};
    bool is_mebx{};
    isobmff_sample_table_layout sample_table{};
    size_t reference_id_offset{};
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
    return time_scale != 0U && duration != 0U;
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
        hdlr.size < hdlr.header_size + 12U) return false;
    const size_t handler_offset = hdlr_start + hdlr.header_size + 8U;
    std::memcpy(output.handler, data.data() + handler_offset, 4U);
    const size_t minf_end = minf_start + minf.size;
    isobmff_box_header stbl{};
    size_t stbl_start = 0;
    if (!unique_child(data, minf_start + minf.header_size, minf_end, "stbl", stbl, stbl_start)) return false;
    output.stbl_start = stbl_start + stbl.header_size;
    output.stbl_end = stbl_start + stbl.size;

    uint32_t description_count = 0;
    if (!parse_sample_descriptions(data, output.stbl_start, output.stbl_end,
            output.is_mebx, output.sample_entry_offset, description_count)) return false;
    if (!output.is_mebx) return true;
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
    if (!unique_child(data, tref_start + tref.header_size, tref_end, "cdsc", cdsc, cdsc_start) ||
        cdsc.size != cdsc.header_size + 4U ||
        tref.size != tref.header_size + cdsc.size) return false;
    output.reference_id_offset = cdsc_start + cdsc.header_size;
    return true;
}

bool parse_file(std::span<const uint8_t> data, file_record& output) noexcept {
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
    for (const auto* track : metadata) {
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
        track_record* source_mebx = nullptr;
        track_record* source_video = nullptr;
        track_record* target_video = nullptr;
        std::vector<track_record*> source_tracks, target_tracks;
        if (!inspect_mebx_tracks(source, source_file, source_tracks, source_video) ||
            !inspect_mebx_tracks(stage_a, target_file, target_tracks, target_video) ||
            source_video == nullptr || target_video == nullptr) return false;
        for (auto* track : source_tracks) if (track->track_id == track_id) source_mebx = track;
        if (source_mebx == nullptr) return false;

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

bool offset_free_static(std::span<const uint8_t> data, const isobmff_box_header& box) {
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
        } else if ((type >> 24U) != 0xA9U) return false;
    }
    return true;
}

bool static_inventory(std::span<const uint8_t> data, size_t begin, size_t end,
    bool video, static_boxes& inventory) {
    static_boxes children;
    inventory.clear();
    if (!children_of(data, begin, end, children)) return false;
    for (const auto& box : children) {
        if (!box_type(data, box.start, "meta") && !box_type(data, box.start, "udta") &&
            !(video && box_type(data, box.start, "tapt"))) continue;
        if (!offset_free_static(data, box)) return false;
        for (const auto& prior : inventory) {
            if (std::memcmp(data.data() + prior.start + 4U, data.data() + box.start + 4U, 4U) == 0) return false;
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
        if (mastering || light) {
            if (child.size != child.header_size + (mastering ? 24U : 4U)) return false;
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
            if (std::memcmp(source.data() + box.start + 4U, target.data() + candidate.start + 4U, 4U) == 0) found = &candidate;
        }
        if (found == nullptr || found->size != box.size ||
            std::memcmp(source.data() + box.start, target.data() + found->start, box.size) != 0) return false;
    }
    return true;
}

bool rewrite_static_parent(std::span<const uint8_t> source, const static_boxes& original,
    std::span<const uint8_t> target, size_t start, size_t end,
    const track_record* video, const std::vector<uint8_t>* rewritten_video,
    std::vector<uint8_t>& result) {
    if (end - start < 8U || read_u32(target.data() + start) != end - start) return false;
    static_boxes children;
    if (!children_of(target, start + 8U, end, children)) return false;
    result.assign(target.begin() + start, target.begin() + start + 8U);
    std::vector<bool> used(original.size(), false);
    for (const auto& child : children) {
        if (video != nullptr && child.start == video->start) {
            result.insert(result.end(), rewritten_video->begin(), rewritten_video->end());
            continue;
        }
        size_t match = original.size();
        for (size_t i = 0U; i < original.size(); ++i) {
            if (std::memcmp(source.data() + original[i].start + 4U, target.data() + child.start + 4U, 4U) == 0) match = i;
        }
        if (match != original.size()) {
            if (used[match]) return false;
            used[match] = true;
            const auto& box = original[match];
            result.insert(result.end(), source.begin() + box.start, source.begin() + box.start + box.size);
        } else result.insert(result.end(), target.begin() + child.start, target.begin() + child.start + child.size);
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

bool validate_isobmff_static_metadata(std::span<const uint8_t> source,
    std::span<const uint8_t> output) noexcept {
    try {
        file_record input{}, target{};
        if (!parse_file(source, input) || !parse_file(output, target)) return false;
        const auto* a = unique_video(input); const auto* b = unique_video(target);
        if (a == nullptr || b == nullptr) return false;
        static_boxes sm, tm, sv, tv, sh, th;
        return static_inventory(source, input.moov_start + input.moov_header_size, input.moov_end, false, sm) &&
            static_inventory(output, target.moov_start + target.moov_header_size, target.moov_end, false, tm) &&
            static_inventory(source, a->start + 8U, a->end, true, sv) &&
            static_inventory(output, b->start + 8U, b->end, true, tv) &&
            hdr_static_inventory(source, *a, sh) && hdr_static_inventory(output, *b, th) && sh.size() == th.size() &&
            same_static(source, sh, output, th) &&
            same_static(source, sm, output, tm) && same_static(source, sv, output, tv);
    } catch (...) { return false; }
}

bool preserve_isobmff_static_metadata(std::span<const uint8_t> source,
    std::span<const uint8_t> stage, std::vector<uint8_t>& output, bool& changed) noexcept {
    output.clear(); changed = false;
    try {
        file_record input{}, target{};
        if (!parse_file(source, input) || !parse_file(stage, target) || target.moov_header_size != 8U) return false;
        const auto* a = unique_video(input); const auto* b = unique_video(target);
        if (a == nullptr || b == nullptr || read_u32(source.data() + a->start) != a->end - a->start) return false;
        static_boxes sm, sv, tm, tv;
        if (!static_inventory(source, input.moov_start + input.moov_header_size, input.moov_end, false, sm) ||
            !static_inventory(source, a->start + 8U, a->end, true, sv) ||
            !static_inventory(stage, target.moov_start + target.moov_header_size, target.moov_end, false, tm) ||
            !static_inventory(stage, b->start + 8U, b->end, true, tv)) return false;
        std::vector<uint8_t> track, movie;
        if (!rewrite_static_parent(source, sv, stage, b->start, b->end, nullptr, nullptr, track) ||
            !rewrite_static_parent(source, sm, stage, target.moov_start, target.moov_end, b, &track, movie)) return false;
        const auto previous = stage.subspan(target.moov_start, target.moov_end - target.moov_start);
        const bool differs = movie.size() != previous.size() || !std::equal(movie.begin(), movie.end(), previous.begin());
        const int64_t delta = static_cast<int64_t>(movie.size()) - static_cast<int64_t>(previous.size());
        if (delta != 0 && !shift_chunk_offsets(movie, 0U, target.moov_end, delta)) return false;
        std::vector<uint8_t> result;
        result.insert(result.end(), stage.begin(), stage.begin() + target.moov_start);
        result.insert(result.end(), movie.begin(), movie.end());
        result.insert(result.end(), stage.begin() + target.moov_end, stage.end());
        if (!validate_isobmff_static_metadata(source, result)) return false;
        output.swap(result); changed = differs;
        return true;
    } catch (...) { output.clear(); changed = false; return false; }
}

} // namespace lpb::media
