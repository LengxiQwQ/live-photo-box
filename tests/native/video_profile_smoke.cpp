#include "containers/isobmff_video_profile.h"
#include "containers/isobmff.h"

#include <algorithm>
#include <array>
#include <cstring>
#include <fstream>
#include <iostream>
#include <limits>
#include <span>
#include <string>
#include <vector>

#undef NDEBUG
#include <cassert>

namespace {

bool read_file(void* user, uint64_t offset, std::span<uint8_t> output) noexcept {
    auto& file = *static_cast<std::ifstream*>(user);
    if (offset > static_cast<uint64_t>(std::numeric_limits<std::streamoff>::max()) ||
        output.size() > static_cast<size_t>(std::numeric_limits<std::streamsize>::max())) return false;
    file.clear();
    file.seekg(static_cast<std::streamoff>(offset), std::ios::beg);
    if (!file.good()) return false;
    file.read(reinterpret_cast<char*>(output.data()), static_cast<std::streamsize>(output.size()));
    return file.gcount() == static_cast<std::streamsize>(output.size());
}

lpb::media::isobmff_video_profile inspect(const char* path) {
    std::ifstream file(path, std::ios::binary | std::ios::ate);
    assert(file.is_open());
    const auto end = static_cast<std::streamoff>(file.tellg());
    assert(end > 0);
    lpb::random_access_reader reader{
        static_cast<uint64_t>(end), &file, read_file};
    lpb::media::isobmff_video_profile profile{};
    lpb::media::video_profile_failure failure{};
    lpb::media::video_profile_diagnostic diagnostic{};
    if (!lpb::media::inspect_isobmff_video_profile(reader, profile, failure, &diagnostic)) {
        std::cerr << "strict profile inspection failed for " << path
            << " with failure category " << static_cast<unsigned>(failure)
            << " at diagnostic stage " << static_cast<unsigned>(diagnostic) << '\n';
        assert(false);
    }
    return profile;
}

bool codec_is(uint32_t value, const char* fourcc) {
    const uint32_t expected = (static_cast<uint32_t>(static_cast<uint8_t>(fourcc[0])) << 24) |
        (static_cast<uint32_t>(static_cast<uint8_t>(fourcc[1])) << 16) |
        (static_cast<uint32_t>(static_cast<uint8_t>(fourcc[2])) << 8) |
        static_cast<uint32_t>(static_cast<uint8_t>(fourcc[3]));
    return value == expected;
}

std::vector<uint8_t> read_bytes(const char* path) {
    std::ifstream file(path, std::ios::binary | std::ios::ate);
    assert(file.is_open());
    const auto end = static_cast<std::streamoff>(file.tellg());
    assert(end > 0 && static_cast<uint64_t>(end) <= static_cast<uint64_t>(std::numeric_limits<size_t>::max()));
    std::vector<uint8_t> bytes(static_cast<size_t>(end));
    file.seekg(0, std::ios::beg);
    file.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    assert(file.gcount() == static_cast<std::streamsize>(bytes.size()));
    return bytes;
}

bool inspect_bytes(const std::vector<uint8_t>& bytes,
    lpb::media::isobmff_video_profile& profile,
    lpb::media::video_profile_failure& failure) {
    lpb::memory_random_access memory{bytes};
    const lpb::random_access_reader reader{
        static_cast<uint64_t>(bytes.size()), &memory, lpb::memory_random_access::read};
    return lpb::media::inspect_isobmff_video_profile(reader, profile, failure);
}

bool prepare_patch(const std::vector<uint8_t>& bytes,
    const lpb::media::isobmff_video_profile& expected,
    lpb::media::media_foundation_cicp_patch& patch,
    lpb::media::video_profile_failure& failure) {
    lpb::memory_random_access memory{bytes};
    const lpb::random_access_reader reader{
        static_cast<uint64_t>(bytes.size()), &memory, lpb::memory_random_access::read};
    return lpb::media::prepare_media_foundation_cicp_patch(reader, expected, patch, failure);
}

void apply_patch(std::vector<uint8_t>& bytes,
    const lpb::media::media_foundation_cicp_patch& patch) {
    assert(patch.changed && patch.moov_offset <= bytes.size() &&
        patch.moov_bytes.size() <= bytes.size() - static_cast<size_t>(patch.moov_offset));
    std::copy(patch.moov_bytes.begin(), patch.moov_bytes.end(),
        bytes.begin() + static_cast<std::ptrdiff_t>(patch.moov_offset));
}

void write_be16(uint8_t* bytes, uint16_t value) {
    bytes[0] = static_cast<uint8_t>(value >> 8);
    bytes[1] = static_cast<uint8_t>(value & 0xffU);
}

void write_be32(uint8_t* bytes, uint32_t value) {
    bytes[0] = static_cast<uint8_t>(value >> 24);
    bytes[1] = static_cast<uint8_t>(value >> 16);
    bytes[2] = static_cast<uint8_t>(value >> 8);
    bytes[3] = static_cast<uint8_t>(value);
}

uint32_t read_be32_value(const uint8_t* bytes) {
    return (static_cast<uint32_t>(bytes[0]) << 24) |
        (static_cast<uint32_t>(bytes[1]) << 16) |
        (static_cast<uint32_t>(bytes[2]) << 8) |
        static_cast<uint32_t>(bytes[3]);
}

void append_be16(std::vector<uint8_t>& bytes, uint16_t value) {
    bytes.push_back(static_cast<uint8_t>(value >> 8));
    bytes.push_back(static_cast<uint8_t>(value));
}

void append_be32(std::vector<uint8_t>& bytes, uint32_t value) {
    bytes.push_back(static_cast<uint8_t>(value >> 24));
    bytes.push_back(static_cast<uint8_t>(value >> 16));
    bytes.push_back(static_cast<uint8_t>(value >> 8));
    bytes.push_back(static_cast<uint8_t>(value));
}

void append_be64(std::vector<uint8_t>& bytes, uint64_t value) {
    for (int shift = 56; shift >= 0; shift -= 8) {
        bytes.push_back(static_cast<uint8_t>(value >> shift));
    }
}

std::vector<uint8_t> make_box(const char type[4], const std::vector<uint8_t>& payload) {
    std::vector<uint8_t> result;
    append_be32(result, static_cast<uint32_t>(payload.size() + 8));
    result.insert(result.end(), type, type + 4);
    result.insert(result.end(), payload.begin(), payload.end());
    return result;
}

void append_bytes(std::vector<uint8_t>& output, const std::vector<uint8_t>& value) {
    output.insert(output.end(), value.begin(), value.end());
}

struct test_edit_entry {
    uint64_t segment_duration{};
    int64_t media_time{};
    uint16_t rate_integer{1};
    uint16_t rate_fraction{};
};

std::vector<uint8_t> make_timeline_fixture(uint32_t movie_timescale, uint32_t media_timescale,
    uint8_t edit_version, const std::vector<test_edit_entry>& edits) {
    std::vector<uint8_t> ftyp_payload{'i', 's', 'o', 'm', 0, 0, 0, 0};
    const auto ftyp = make_box("ftyp", ftyp_payload);
    const auto mdat = make_box("mdat", std::vector<uint8_t>{'V', 'I', 'D', '0'});

    std::vector<uint8_t> mvhd_payload(4, 0);
    append_be32(mvhd_payload, 0);
    append_be32(mvhd_payload, 0);
    append_be32(mvhd_payload, movie_timescale);
    append_be32(mvhd_payload, 1);
    const auto mvhd = make_box("mvhd", mvhd_payload);

    std::vector<uint8_t> mdhd_payload(4, 0);
    append_be32(mdhd_payload, 0);
    append_be32(mdhd_payload, 0);
    append_be32(mdhd_payload, media_timescale);
    append_be32(mdhd_payload, 1);
    append_be16(mdhd_payload, 0);
    append_be16(mdhd_payload, 0);
    const auto mdhd = make_box("mdhd", mdhd_payload);

    std::vector<uint8_t> hdlr_payload(4, 0);
    append_be32(hdlr_payload, 0);
    hdlr_payload.insert(hdlr_payload.end(), {'v', 'i', 'd', 'e'});
    const auto hdlr = make_box("hdlr", hdlr_payload);

    std::vector<uint8_t> stsd_payload(4, 0);
    append_be32(stsd_payload, 1);
    append_bytes(stsd_payload, make_box("avc1", {}));
    const auto stsd = make_box("stsd", stsd_payload);

    std::vector<uint8_t> stts_payload(4, 0);
    append_be32(stts_payload, 1);
    append_be32(stts_payload, 1);
    append_be32(stts_payload, 1);
    const auto stts = make_box("stts", stts_payload);

    std::vector<uint8_t> stsc_payload(4, 0);
    append_be32(stsc_payload, 1);
    append_be32(stsc_payload, 1);
    append_be32(stsc_payload, 1);
    append_be32(stsc_payload, 1);
    const auto stsc = make_box("stsc", stsc_payload);

    std::vector<uint8_t> stsz_payload(4, 0);
    append_be32(stsz_payload, 4);
    append_be32(stsz_payload, 1);
    const auto stsz = make_box("stsz", stsz_payload);

    std::vector<uint8_t> stco_payload(4, 0);
    append_be32(stco_payload, 1);
    append_be32(stco_payload, static_cast<uint32_t>(ftyp.size() + 8));
    const auto stco = make_box("stco", stco_payload);

    std::vector<uint8_t> stbl_payload;
    append_bytes(stbl_payload, stsd);
    append_bytes(stbl_payload, stts);
    append_bytes(stbl_payload, stsc);
    append_bytes(stbl_payload, stsz);
    append_bytes(stbl_payload, stco);
    const auto stbl = make_box("stbl", stbl_payload);
    const auto minf = make_box("minf", stbl);

    std::vector<uint8_t> mdia_payload;
    append_bytes(mdia_payload, mdhd);
    append_bytes(mdia_payload, hdlr);
    append_bytes(mdia_payload, minf);
    const auto mdia = make_box("mdia", mdia_payload);

    std::vector<uint8_t> trak_payload;
    if (!edits.empty()) {
        std::vector<uint8_t> elst_payload(4, 0);
        elst_payload[0] = edit_version;
        append_be32(elst_payload, static_cast<uint32_t>(edits.size()));
        for (const auto& edit : edits) {
            if (edit_version == 0) {
                append_be32(elst_payload, static_cast<uint32_t>(edit.segment_duration));
                append_be32(elst_payload, static_cast<uint32_t>(edit.media_time));
            } else {
                append_be64(elst_payload, edit.segment_duration);
                append_be64(elst_payload, static_cast<uint64_t>(edit.media_time));
            }
            append_be16(elst_payload, edit.rate_integer);
            append_be16(elst_payload, edit.rate_fraction);
        }
        const auto elst = make_box("elst", elst_payload);
        append_bytes(trak_payload, make_box("edts", elst));
    }
    append_bytes(trak_payload, mdia);
    const auto trak = make_box("trak", trak_payload);
    std::vector<uint8_t> moov_payload;
    append_bytes(moov_payload, mvhd);
    append_bytes(moov_payload, trak);
    const auto moov = make_box("moov", moov_payload);

    std::vector<uint8_t> result;
    append_bytes(result, ftyp);
    append_bytes(result, mdat);
    append_bytes(result, moov);
    return result;
}

struct test_box {
    size_t start{};
    size_t size{};
    size_t header_size{};
};

bool box_at(const std::vector<uint8_t>& bytes, size_t position, size_t end, test_box& box);
bool box_is(const std::vector<uint8_t>& bytes, const test_box& box, const char* type);
bool child_boxes(const std::vector<uint8_t>& bytes, size_t start, size_t end,
    std::vector<test_box>& boxes, bool allow_four_zero_padding = false);
bool unique_child(const std::vector<uint8_t>& bytes, const std::vector<test_box>& boxes,
    const char* type, test_box& output);

struct video_timing_boxes {
    test_box stts{};
    test_box ctts{};
    test_box elst{};
};

bool find_video_timing_boxes(const std::vector<uint8_t>& bytes, video_timing_boxes& output) {
    output = {};
    std::vector<test_box> top;
    test_box moov{};
    if (!child_boxes(bytes, 0, bytes.size(), top) || !unique_child(bytes, top, "moov", moov)) return false;
    std::vector<test_box> moov_children;
    if (!child_boxes(bytes, moov.start + moov.header_size, moov.start + moov.size, moov_children)) return false;
    size_t video_tracks = 0;
    for (const auto& trak : moov_children) {
        if (!box_is(bytes, trak, "trak")) continue;
        std::vector<test_box> trak_children;
        test_box mdia{};
        if (!child_boxes(bytes, trak.start + trak.header_size, trak.start + trak.size, trak_children) ||
            !unique_child(bytes, trak_children, "mdia", mdia)) return false;
        std::vector<test_box> mdia_children;
        test_box hdlr{};
        if (!child_boxes(bytes, mdia.start + mdia.header_size, mdia.start + mdia.size, mdia_children) ||
            !unique_child(bytes, mdia_children, "hdlr", hdlr) || hdlr.size < hdlr.header_size + 12) return false;
        if (std::memcmp(bytes.data() + hdlr.start + hdlr.header_size + 8, "vide", 4) != 0) continue;
        ++video_tracks;
        std::vector<test_box> trak_children_for_video;
        test_box edts{};
        if (!child_boxes(bytes, trak.start + trak.header_size, trak.start + trak.size,
                trak_children_for_video)) return false;
        for (const auto& candidate : trak_children_for_video) {
            if (box_is(bytes, candidate, "edts")) edts = candidate;
        }
        if (edts.size != 0) {
            std::vector<test_box> edit_children;
            if (!child_boxes(bytes, edts.start + edts.header_size, edts.start + edts.size, edit_children) ||
                !unique_child(bytes, edit_children, "elst", output.elst)) return false;
        }
        test_box minf{};
        if (!unique_child(bytes, mdia_children, "minf", minf)) return false;
        std::vector<test_box> minf_children;
        test_box stbl{};
        if (!child_boxes(bytes, minf.start + minf.header_size, minf.start + minf.size, minf_children) ||
            !unique_child(bytes, minf_children, "stbl", stbl)) return false;
        std::vector<test_box> stbl_children;
        if (!child_boxes(bytes, stbl.start + stbl.header_size, stbl.start + stbl.size, stbl_children) ||
            !unique_child(bytes, stbl_children, "stts", output.stts)) return false;
        size_t ctts_count = 0;
        for (const auto& candidate : stbl_children) {
            if (box_is(bytes, candidate, "ctts")) {
                output.ctts = candidate;
                ++ctts_count;
            }
        }
        if (ctts_count > 1) return false;
    }
    return video_tracks == 1;
}

void verify_source_presentation_timeline(const char* apple_path, const char* vivo_path) {
    auto apple_bytes = read_bytes(apple_path);
    isobmff_video_presentation_timeline apple{};
    assert(inspect_isobmff_video_presentation_timeline(apple_bytes, apple));
    assert(apple.movie_timescale == 600 && apple.media_timescale == 600 &&
        apple.sample_count == 61 && apple.visible_sample_count == 55 &&
        apple.presentation_order.size() == 61 && apple.media_edit_start_ticks == 160 &&
        apple.leading_empty_duration_ticks == 0);

    std::vector<int64_t> apple_expected_pts;
    for (int64_t pts = 0; pts <= 480; pts += 20) apple_expected_pts.push_back(pts);
    for (int64_t pts = 520; pts <= 980; pts += 20) apple_expected_pts.push_back(pts);
    for (int64_t pts = 1020; pts <= 1120; pts += 20) apple_expected_pts.push_back(pts);
    assert(apple_expected_pts.size() == 55);
    size_t visible_index = 0;
    size_t hidden_count = 0;
    for (size_t index = 0; index < apple.presentation_order.size(); ++index) {
        const auto& entry = apple.presentation_order[index];
        if (!entry.visible) {
            ++hidden_count;
            continue;
        }
        assert(visible_index < apple_expected_pts.size());
        assert(entry.presentation_time_ticks - apple.media_edit_start_ticks == apple_expected_pts[visible_index]);
        const int64_t expected_delta = visible_index + 1 < apple_expected_pts.size()
            ? apple_expected_pts[visible_index + 1] - apple_expected_pts[visible_index] : 20;
        assert(entry.presentation_duration_ticks == static_cast<uint64_t>(expected_delta));
        ++visible_index;
    }
    assert(hidden_count == 6 && visible_index == apple_expected_pts.size());

    const auto vivo_bytes = read_bytes(vivo_path);
    isobmff_video_presentation_timeline vivo{};
    assert(inspect_isobmff_video_presentation_timeline(vivo_bytes, vivo));
    constexpr std::array<int64_t, 61> vivo_expected_pts{
        0, 2974, 6015, 8964, 11996, 14998, 17974, 21053, 24048, 27043,
        29973, 33005, 35977, 39042, 42021, 45021, 48021, 54055, 57045, 60035,
        63025, 66003, 69035, 72067, 75039, 78032, 81068, 84039, 87041, 90060,
        93046, 96001, 99040, 102058, 105034, 108055, 111043, 114053, 117063, 120048,
        123049, 126035, 129052, 132028, 135079, 138017, 141012, 144007, 147022, 150069,
        153080, 156052, 159024, 162040, 165019, 168033, 171047, 174061, 177028, 180096,
        183077};
    assert(vivo.movie_timescale == 10000 && vivo.media_timescale == 90000 &&
        vivo.sample_count == vivo_expected_pts.size() && vivo.visible_sample_count == vivo_expected_pts.size() &&
        vivo.presentation_order.size() == vivo_expected_pts.size() && vivo.media_edit_start_ticks == 0 &&
        vivo.leading_empty_duration_ticks == 0);
    visible_index = 0;
    for (const auto& entry : vivo.presentation_order) {
        assert(entry.visible && visible_index < vivo_expected_pts.size());
        assert(entry.presentation_time_ticks == vivo_expected_pts[visible_index]);
        const int64_t expected_delta = visible_index + 1 < vivo_expected_pts.size()
            ? vivo_expected_pts[visible_index + 1] - vivo_expected_pts[visible_index] : 2981;
        assert(entry.presentation_duration_ticks == static_cast<uint64_t>(expected_delta));
        ++visible_index;
    }
    assert(visible_index == vivo_expected_pts.size());

    video_timing_boxes boxes{};
    assert(find_video_timing_boxes(apple_bytes, boxes) && boxes.stts.size != 0 &&
        boxes.ctts.size != 0 && boxes.elst.size != 0);
    auto malformed_stts = apple_bytes;
    write_be32(malformed_stts.data() + boxes.stts.start + boxes.stts.header_size + 12, 0);
    isobmff_video_presentation_timeline rejected{};
    assert(!inspect_isobmff_video_presentation_timeline(malformed_stts, rejected));

    auto bad_ctts_coverage = apple_bytes;
    const size_t ctts_first_count = boxes.ctts.start + boxes.ctts.header_size + 8;
    write_be32(bad_ctts_coverage.data() + ctts_first_count,
        read_be32_value(bad_ctts_coverage.data() + ctts_first_count) + 1U);
    assert(!inspect_isobmff_video_presentation_timeline(bad_ctts_coverage, rejected));

    auto unsupported_rate = apple_bytes;
    const size_t elst_rate = boxes.elst.start + boxes.elst.header_size + 16;
    write_be16(unsupported_rate.data() + elst_rate, 2);
    assert(!inspect_isobmff_video_presentation_timeline(unsupported_rate, rejected));

    const auto overflow_edit = make_timeline_fixture(1, 2, 1, {
        {std::numeric_limits<uint64_t>::max(), 0, 1, 0}});
    assert(!inspect_isobmff_video_presentation_timeline(overflow_edit, rejected));
    const auto ambiguous_edits = make_timeline_fixture(1, 1, 0, {
        {1, 0, 1, 0}, {1, 0, 1, 0}});
    assert(!inspect_isobmff_video_presentation_timeline(ambiguous_edits, rejected));
    const auto supported_leading_empty = make_timeline_fixture(1, 1, 0, {
        {10, -1, 1, 0}, {1, 0, 1, 0}});
    isobmff_video_presentation_timeline leading_empty{};
    assert(inspect_isobmff_video_presentation_timeline(supported_leading_empty, leading_empty));
    assert(leading_empty.sample_count == 1 && leading_empty.visible_sample_count == 1 &&
        leading_empty.presentation_order[0].normalized_time_100ns == 100'000'000LL);
}

bool box_at(const std::vector<uint8_t>& bytes, size_t position, size_t end, test_box& box) {
    if (position > end || end - position < 8) return false;
    uint64_t size = (static_cast<uint64_t>(bytes[position]) << 24) |
        (static_cast<uint64_t>(bytes[position + 1]) << 16) |
        (static_cast<uint64_t>(bytes[position + 2]) << 8) | bytes[position + 3];
    size_t header_size = 8;
    if (size == 1) {
        if (end - position < 16) return false;
        size = 0;
        for (size_t i = 0; i < 8; ++i) size = (size << 8) | bytes[position + 8 + i];
        header_size = 16;
    } else if (size == 0) {
        size = end - position;
    }
    if (size < header_size || size > end - position) return false;
    box = {position, static_cast<size_t>(size), header_size};
    return true;
}

bool box_is(const std::vector<uint8_t>& bytes, const test_box& box, const char* type) {
    return box.start <= bytes.size() && box.size >= box.header_size &&
        box.header_size >= 8 && std::memcmp(bytes.data() + box.start + 4, type, 4) == 0;
}

bool child_boxes(const std::vector<uint8_t>& bytes, size_t start, size_t end,
    std::vector<test_box>& boxes, bool allow_four_zero_padding) {
    boxes.clear();
    if (start > end || end > bytes.size()) return false;
    size_t position = start;
    while (position < end) {
        if (allow_four_zero_padding && end - position == 4 &&
            bytes[position] == 0 && bytes[position + 1] == 0 &&
            bytes[position + 2] == 0 && bytes[position + 3] == 0) {
            position = end;
            break;
        }
        test_box box{};
        if (!box_at(bytes, position, end, box)) return false;
        boxes.push_back(box);
        position += box.size;
    }
    return position == end;
}

bool unique_child(const std::vector<uint8_t>& bytes, const std::vector<test_box>& boxes,
    const char* type, test_box& output) {
    size_t count = 0;
    for (const auto& box : boxes) {
        if (!box_is(bytes, box, type)) continue;
        output = box;
        ++count;
    }
    return count == 1;
}

uint64_t read_box_size(const std::vector<uint8_t>& bytes, const test_box& box) {
    if (box.header_size == 8) {
        return (static_cast<uint64_t>(bytes[box.start]) << 24) |
            (static_cast<uint64_t>(bytes[box.start + 1]) << 16) |
            (static_cast<uint64_t>(bytes[box.start + 2]) << 8) | bytes[box.start + 3];
    }
    uint64_t size = 0;
    for (size_t i = 0; i < 8; ++i) size = (size << 8) | bytes[box.start + 8 + i];
    return size;
}

bool increase_box_size(std::vector<uint8_t>& bytes, const test_box& box, size_t amount) {
    const uint64_t current = read_box_size(bytes, box);
    if (current == 0 || current > (std::numeric_limits<uint64_t>::max)() - amount) return false;
    const uint64_t updated = current + amount;
    if (box.header_size == 8) {
        if (updated > 0xffffffffULL) return false;
        bytes[box.start] = static_cast<uint8_t>(updated >> 24);
        bytes[box.start + 1] = static_cast<uint8_t>(updated >> 16);
        bytes[box.start + 2] = static_cast<uint8_t>(updated >> 8);
        bytes[box.start + 3] = static_cast<uint8_t>(updated);
        return true;
    }
    for (size_t i = 0; i < 8; ++i) {
        bytes[box.start + 8 + i] = static_cast<uint8_t>(updated >> (56 - i * 8));
    }
    return true;
}

bool make_duplicate_colr_file(const std::vector<uint8_t>& original,
    std::vector<uint8_t>& duplicate) {
    std::vector<test_box> top_boxes;
    if (!child_boxes(original, 0, original.size(), top_boxes)) return false;
    test_box moov{};
    if (!unique_child(original, top_boxes, "moov", moov)) return false;

    std::vector<test_box> moov_children;
    if (!child_boxes(original, moov.start + moov.header_size, moov.start + moov.size, moov_children)) return false;
    test_box video_trak{};
    size_t video_track_count = 0;
    for (const auto& trak : moov_children) {
        if (!box_is(original, trak, "trak")) continue;
        std::vector<test_box> trak_children;
        if (!child_boxes(original, trak.start + trak.header_size, trak.start + trak.size, trak_children)) return false;
        test_box mdia{};
        if (!unique_child(original, trak_children, "mdia", mdia)) return false;
        std::vector<test_box> mdia_children;
        if (!child_boxes(original, mdia.start + mdia.header_size, mdia.start + mdia.size, mdia_children)) return false;
        test_box hdlr{};
        if (!unique_child(original, mdia_children, "hdlr", hdlr) ||
            hdlr.size < hdlr.header_size + 12) return false;
        if (std::memcmp(original.data() + hdlr.start + hdlr.header_size + 8, "vide", 4) == 0) {
            video_trak = trak;
            ++video_track_count;
        }
    }
    if (video_track_count != 1) return false;

    std::vector<test_box> trak_children;
    if (!child_boxes(original, video_trak.start + video_trak.header_size,
            video_trak.start + video_trak.size, trak_children)) return false;
    test_box mdia{};
    if (!unique_child(original, trak_children, "mdia", mdia)) return false;
    std::vector<test_box> mdia_children;
    if (!child_boxes(original, mdia.start + mdia.header_size, mdia.start + mdia.size, mdia_children)) return false;
    test_box minf{};
    if (!unique_child(original, mdia_children, "minf", minf)) return false;
    std::vector<test_box> minf_children;
    if (!child_boxes(original, minf.start + minf.header_size, minf.start + minf.size, minf_children)) return false;
    test_box stbl{};
    if (!unique_child(original, minf_children, "stbl", stbl)) return false;
    std::vector<test_box> stbl_children;
    if (!child_boxes(original, stbl.start + stbl.header_size, stbl.start + stbl.size, stbl_children)) return false;
    test_box stsd{};
    if (!unique_child(original, stbl_children, "stsd", stsd) || stsd.size < stsd.header_size + 8) return false;
    const size_t stsd_body = stsd.start + stsd.header_size;
    if ((original[stsd_body] | original[stsd_body + 1] | original[stsd_body + 2] | original[stsd_body + 3]) != 0) return false;
    const uint32_t entry_count = (static_cast<uint32_t>(original[stsd_body + 4]) << 24) |
        (static_cast<uint32_t>(original[stsd_body + 5]) << 16) |
        (static_cast<uint32_t>(original[stsd_body + 6]) << 8) | original[stsd_body + 7];
    if (entry_count != 1) return false;
    test_box entry{};
    const size_t entry_start = stsd_body + 8;
    if (!box_at(original, entry_start, stsd.start + stsd.size, entry) ||
        entry.size < entry.header_size + 78) return false;
    std::vector<test_box> sample_children;
    if (!child_boxes(original, entry.start + entry.header_size + 78,
            entry.start + entry.size, sample_children, true)) return false;
    test_box colr{};
    if (!unique_child(original, sample_children, "colr", colr)) return false;

    size_t insertion = entry.start + entry.size;
    if (entry.size >= 4 && original[insertion - 1] == 0 && original[insertion - 2] == 0 &&
        original[insertion - 3] == 0 && original[insertion - 4] == 0) {
        insertion -= 4;
    }
    std::vector<uint8_t> duplicate_colr(original.begin() + static_cast<std::ptrdiff_t>(colr.start),
        original.begin() + static_cast<std::ptrdiff_t>(colr.start + colr.size));
    duplicate = original;
    duplicate.insert(duplicate.begin() + static_cast<std::ptrdiff_t>(insertion),
        duplicate_colr.begin(), duplicate_colr.end());
    const std::array<test_box, 7> ancestors{entry, stsd, stbl, minf, mdia, video_trak, moov};
    for (const auto& ancestor : ancestors) {
        if (!increase_box_size(duplicate, ancestor, duplicate_colr.size())) return false;
    }
    return true;
}

bool find_sps_header_in_config(const std::vector<uint8_t>& bytes,
    size_t config_offset, bool avc, size_t& sps_header_offset) {
    if (config_offset >= bytes.size()) return false;
    size_t position = config_offset;
    if (avc) {
        if (bytes.size() - position < 7 || bytes[position] != 1) return false;
        const uint8_t count = bytes[position + 5] & 0x1fU;
        position += 6;
        if (count == 0) return false;
        for (uint8_t i = 0; i < count; ++i) {
            if (bytes.size() - position < 2) return false;
            const uint16_t length = static_cast<uint16_t>((bytes[position] << 8) | bytes[position + 1]);
            position += 2;
            if (length == 0 || length > bytes.size() - position) return false;
            if ((bytes[position] & 0x1fU) == 7) {
                sps_header_offset = position;
                return true;
            }
            position += length;
        }
        return false;
    }

    if (bytes.size() - position < 23 || bytes[position] != 1) return false;
    position += 23;
    const uint8_t array_count = bytes[config_offset + 22];
    for (uint8_t array = 0; array < array_count; ++array) {
        if (bytes.size() - position < 3) return false;
        const uint8_t type = bytes[position++] & 0x3fU;
        const uint16_t count = static_cast<uint16_t>((bytes[position] << 8) | bytes[position + 1]);
        position += 2;
        if (count == 0) return false;
        for (uint16_t i = 0; i < count; ++i) {
            if (bytes.size() - position < 2) return false;
            const uint16_t length = static_cast<uint16_t>((bytes[position] << 8) | bytes[position + 1]);
            position += 2;
            if (length == 0 || length > bytes.size() - position) return false;
            if (type == 33) {
                sps_header_offset = position;
                return true;
            }
            position += length;
        }
    }
    return false;
}

void verify_cicp_alias_patch(const char* path, uint16_t alias_transfer, uint16_t alias_matrix) {
    const std::vector<uint8_t> original = read_bytes(path);
    lpb::media::isobmff_video_profile source{};
    lpb::media::video_profile_failure failure{};
    assert(inspect_bytes(original, source, failure));
    assert(source.classification == lpb::media::video_profile_class::ordinary_sdr &&
        source.bit_depth_luma == 8 && source.bit_depth_chroma == 8 && source.full_range_known == 1);

    lpb::media::media_foundation_cicp_patch no_op{};
    assert(prepare_patch(original, source, no_op, failure));
    assert(!no_op.changed && no_op.moov_bytes.empty());

    auto collapsed = source;
    collapsed.transfer_characteristics = alias_transfer;
    collapsed.matrix_coefficients = alias_matrix;
    lpb::media::media_foundation_cicp_patch collapse_patch{};
    assert(prepare_patch(original, collapsed, collapse_patch, failure));
    assert(collapse_patch.changed && !collapse_patch.moov_bytes.empty());
    auto aliased_bytes = original;
    apply_patch(aliased_bytes, collapse_patch);
    lpb::media::isobmff_video_profile aliased{};
    assert(inspect_bytes(aliased_bytes, aliased, failure));
    assert(aliased.color_primaries == source.color_primaries &&
        aliased.transfer_characteristics == alias_transfer && aliased.matrix_coefficients == alias_matrix &&
        aliased.bitstream_color_description_present != 0 &&
        aliased.bitstream_color_primaries == source.color_primaries &&
        aliased.bitstream_transfer_characteristics == alias_transfer &&
        aliased.bitstream_matrix_coefficients == alias_matrix &&
        aliased.full_range == source.full_range && aliased.full_range_known == source.full_range_known);
    const bool avc = codec_is(source.codec_fourcc, "avc1") || codec_is(source.codec_fourcc, "avc3");

    auto conflicting_bytes = aliased_bytes;
    const size_t color_payload = static_cast<size_t>(collapse_patch.moov_offset) + collapse_patch.color_payload_offset;
    write_be16(conflicting_bytes.data() + color_payload + 4, source.color_primaries);
    write_be16(conflicting_bytes.data() + color_payload + 6, source.transfer_characteristics);
    write_be16(conflicting_bytes.data() + color_payload + 8, source.matrix_coefficients);
    lpb::media::isobmff_video_profile conflicting{};
    assert(!inspect_bytes(conflicting_bytes, conflicting, failure));
    assert(failure == lpb::media::video_profile_failure::ambiguous);

    auto malformed_sps = original;
    size_t sps_header_offset = 0;
    const size_t config_offset = static_cast<size_t>(collapse_patch.moov_offset) + collapse_patch.codec_config_offset;
    assert(find_sps_header_in_config(original, config_offset, avc, sps_header_offset));
    malformed_sps[sps_header_offset] = static_cast<uint8_t>(malformed_sps[sps_header_offset] | 0x80U);
    assert(!prepare_patch(malformed_sps, source, no_op, failure));
    assert(failure == lpb::media::video_profile_failure::malformed);

    auto malformed_color = original;
    malformed_color[static_cast<size_t>(collapse_patch.moov_offset) + collapse_patch.color_payload_offset] = 'x';
    assert(!prepare_patch(malformed_color, source, no_op, failure));
    assert(failure != lpb::media::video_profile_failure::none);

    std::vector<uint8_t> duplicate_color;
    assert(make_duplicate_colr_file(original, duplicate_color));
    assert(!prepare_patch(duplicate_color, source, no_op, failure));
    assert(failure == lpb::media::video_profile_failure::ambiguous);

    auto restored_patch_source = aliased_bytes;
    lpb::media::media_foundation_cicp_patch restore_patch{};
    assert(prepare_patch(restored_patch_source, source, restore_patch, failure));
    assert(restore_patch.changed);
    apply_patch(restored_patch_source, restore_patch);
    lpb::media::isobmff_video_profile restored{};
    assert(inspect_bytes(restored_patch_source, restored, failure));
    assert(restored.color_primaries == source.color_primaries &&
        restored.transfer_characteristics == source.transfer_characteristics &&
        restored.matrix_coefficients == source.matrix_coefficients &&
        restored.full_range == source.full_range && restored.full_range_known == source.full_range_known);

    const auto expect_rejected = [&](lpb::media::isobmff_video_profile wrong) {
        lpb::media::media_foundation_cicp_patch rejected{};
        assert(!prepare_patch(original, wrong, rejected, failure));
        assert(!rejected.changed && rejected.moov_bytes.empty());
    };
    auto wrong = source;
    wrong.color_primaries = source.color_primaries == 12 ? 9 : 12;
    expect_rejected(wrong);
    wrong = source;
    wrong.transfer_characteristics = alias_transfer == 1 ? 5 : 1;
    expect_rejected(wrong);
    wrong = source;
    wrong.matrix_coefficients = alias_matrix == 5 ? 1 : 0;
    expect_rejected(wrong);
    wrong = source;
    wrong.full_range = static_cast<uint8_t>(source.full_range == 0 ? 1 : 0);
    expect_rejected(wrong);
}

void verify_cicp_patch_mode_selection() {
    const auto make_profile = [](uint32_t codec, uint16_t primaries, uint16_t transfer,
        uint16_t matrix) {
        lpb::media::isobmff_video_profile profile{};
        profile.codec_fourcc = codec;
        profile.color_primaries = primaries;
        profile.transfer_characteristics = transfer;
        profile.matrix_coefficients = matrix;
        profile.bit_depth_luma = 8;
        profile.bit_depth_chroma = 8;
        profile.full_range = 1;
        profile.full_range_known = 1;
        profile.classification = lpb::media::video_profile_class::ordinary_sdr;
        return profile;
    };

    const auto apple_source = make_profile(0x68766331U, 12, 1, 6);
    auto apple_mf = make_profile(0x61766331U, 12, 1, 5);
    assert(lpb::media::select_media_foundation_cicp_patch_mode(apple_mf, apple_source) ==
        lpb::media::media_foundation_cicp_patch_mode::colr_only);

    const auto vivo_source = make_profile(0x61766331U, 5, 6, 6);
    auto vivo_mf = make_profile(0x68766331U, 5, 1, 5);
    assert(lpb::media::select_media_foundation_cicp_patch_mode(vivo_mf, vivo_source) ==
        lpb::media::media_foundation_cicp_patch_mode::colr_only);

    auto exact_without_bitstream_color = apple_source;
    exact_without_bitstream_color.codec_fourcc = 0x61766331U;
    assert(lpb::media::select_media_foundation_cicp_patch_mode(
        exact_without_bitstream_color, apple_source) ==
        lpb::media::media_foundation_cicp_patch_mode::unchanged);

    auto explicit_bitstream_color = apple_mf;
    explicit_bitstream_color.bitstream_color_description_present = 1;
    explicit_bitstream_color.bitstream_color_primaries = apple_mf.color_primaries;
    explicit_bitstream_color.bitstream_transfer_characteristics = apple_mf.transfer_characteristics;
    explicit_bitstream_color.bitstream_matrix_coefficients = apple_mf.matrix_coefficients;
    assert(lpb::media::select_media_foundation_cicp_patch_mode(explicit_bitstream_color, apple_source) ==
        lpb::media::media_foundation_cicp_patch_mode::colr_and_bitstream);

    auto unspecified_bitstream_component = explicit_bitstream_color;
    unspecified_bitstream_component.bitstream_matrix_coefficients = 2;
    assert(lpb::media::select_media_foundation_cicp_patch_mode(
        unspecified_bitstream_component, apple_source) ==
        lpb::media::media_foundation_cicp_patch_mode::colr_and_bitstream);

    auto invalid = apple_mf;
    invalid.bitstream_transfer_characteristics = 1;
    assert(lpb::media::select_media_foundation_cicp_patch_mode(invalid, apple_source) ==
        lpb::media::media_foundation_cicp_patch_mode::reject);
    invalid = apple_mf;
    invalid.bitstream_color_primaries = 12;
    assert(lpb::media::select_media_foundation_cicp_patch_mode(invalid, apple_source) ==
        lpb::media::media_foundation_cicp_patch_mode::reject);
    invalid = apple_mf;
    invalid.bitstream_color_description_present = 2;
    assert(lpb::media::select_media_foundation_cicp_patch_mode(invalid, apple_source) ==
        lpb::media::media_foundation_cicp_patch_mode::reject);
    invalid = explicit_bitstream_color;
    invalid.bitstream_matrix_coefficients = 4;
    assert(lpb::media::select_media_foundation_cicp_patch_mode(invalid, apple_source) ==
        lpb::media::media_foundation_cicp_patch_mode::reject);
    invalid = apple_mf;
    invalid.transfer_characteristics = 5;
    assert(lpb::media::select_media_foundation_cicp_patch_mode(invalid, apple_source) ==
        lpb::media::media_foundation_cicp_patch_mode::reject);
    invalid = apple_mf;
    invalid.matrix_coefficients = 1;
    assert(lpb::media::select_media_foundation_cicp_patch_mode(invalid, apple_source) ==
        lpb::media::media_foundation_cicp_patch_mode::reject);
    invalid = apple_mf;
    invalid.color_primaries = 9;
    assert(lpb::media::select_media_foundation_cicp_patch_mode(invalid, apple_source) ==
        lpb::media::media_foundation_cicp_patch_mode::reject);
    invalid = apple_mf;
    invalid.full_range = 0;
    assert(lpb::media::select_media_foundation_cicp_patch_mode(invalid, apple_source) ==
        lpb::media::media_foundation_cicp_patch_mode::reject);
}

} // namespace

int main(int argc, char** argv) {
    assert(argc == 4);
    verify_cicp_patch_mode_selection();
    verify_source_presentation_timeline(argv[1], argv[2]);

    const auto apple = inspect(argv[1]);
    assert((codec_is(apple.codec_fourcc, "hvc1") || codec_is(apple.codec_fourcc, "hev1")) &&
        apple.bit_depth_luma == 8 && apple.bit_depth_chroma == 8 && apple.full_range == 1 && apple.full_range_known == 1 &&
        apple.color_primaries == 12 && apple.transfer_characteristics == 1 &&
        apple.matrix_coefficients == 6 &&
        apple.classification == lpb::media::video_profile_class::ordinary_sdr);
    assert(lpb::media::select_video_conversion_owner(apple, lpb::media::requested_video_codec::copy) ==
        lpb::media::video_conversion_owner::project_remux);
    assert(lpb::media::select_video_conversion_owner(apple, lpb::media::requested_video_codec::hevc) ==
        lpb::media::video_conversion_owner::media_foundation_software);
    assert(lpb::media::select_video_conversion_owner(apple, lpb::media::requested_video_codec::h264) ==
        lpb::media::video_conversion_owner::media_foundation_software);

    const auto vivo = inspect(argv[2]);
    assert((codec_is(vivo.codec_fourcc, "avc1") || codec_is(vivo.codec_fourcc, "avc3")) &&
        vivo.bit_depth_luma == 8 && vivo.bit_depth_chroma == 8 && vivo.full_range == 1 && vivo.full_range_known == 1 &&
        vivo.color_primaries == 5 && vivo.transfer_characteristics == 6 &&
        vivo.matrix_coefficients == 6 &&
        vivo.classification == lpb::media::video_profile_class::ordinary_sdr);
    assert(lpb::media::select_video_conversion_owner(vivo, lpb::media::requested_video_codec::copy) ==
        lpb::media::video_conversion_owner::project_remux);
    assert(lpb::media::select_video_conversion_owner(vivo, lpb::media::requested_video_codec::hevc) ==
        lpb::media::video_conversion_owner::media_foundation_software);
    assert(lpb::media::select_video_conversion_owner(vivo, lpb::media::requested_video_codec::h264) ==
        lpb::media::video_conversion_owner::media_foundation_software);

    verify_cicp_alias_patch(argv[1], 1, 5);
    verify_cicp_alias_patch(argv[2], 1, 5);

    const auto huawei = inspect(argv[3]);
    assert((codec_is(huawei.codec_fourcc, "hvc1") || codec_is(huawei.codec_fourcc, "hev1")) &&
        huawei.bit_depth_luma == 10 && huawei.bit_depth_chroma == 10 && huawei.full_range == 1 && huawei.full_range_known == 1 &&
        huawei.color_primaries == 9 && huawei.transfer_characteristics == 18 &&
        huawei.matrix_coefficients == 9 &&
        huawei.classification == lpb::media::video_profile_class::preservation_critical);
    assert(lpb::media::select_video_conversion_owner(huawei, lpb::media::requested_video_codec::copy) ==
        lpb::media::video_conversion_owner::project_remux);
    assert(lpb::media::select_video_conversion_owner(huawei, lpb::media::requested_video_codec::hevc) ==
        lpb::media::video_conversion_owner::minimal_libav_sidecar);
    assert(lpb::media::select_video_conversion_owner(huawei, lpb::media::requested_video_codec::h264) ==
        lpb::media::video_conversion_owner::minimal_libav_sidecar);

    lpb::media::isobmff_video_profile malformed{};
    lpb::media::video_profile_failure failure{};
    constexpr std::array<uint8_t, 16> truncated{
        0, 0, 0, 8, 'm', 'o', 'o', 'v', 0, 0, 0, 8, 'm', 'd', 'a', 't'};
    lpb::memory_random_access memory{truncated};
    assert(!lpb::media::inspect_isobmff_video_profile(memory.view(), malformed, failure));
    assert(failure != lpb::media::video_profile_failure::none);
    assert(lpb::media::select_video_conversion_owner(malformed, lpb::media::requested_video_codec::hevc) ==
        lpb::media::video_conversion_owner::reject);

    std::cout << "P5R5 strict input profile inspection and owner selection passed for Apple, Vivo, and Huawei RealSamples.\n";
    return 0;
}
