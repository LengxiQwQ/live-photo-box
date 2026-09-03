#include "media/media_cleaner.h"
#include "protocols/clean/xmp_cleaner.h"
#include "protocols/clean/samsung_sef_cleaner.h"
#include "protocols/clean/heif_cleaner.h"
#include "protocols/clean/jpeg_structure_cleaner.h"
#include "foundation/internal.h"
#include "binary/binary_io.h"
#include "metadata/jpeg.h"
#include "containers/isobmff.h"

#include <fstream>
#include <filesystem>
#include <vector>
#include <string>
#include <cstring>
#include <limits>
#include <random>
#include <string_view>
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>

namespace fs = std::filesystem;

namespace lpb::media {

static void add_fact(
    std::vector<lpb_removed_protocol_fact>& out_facts,
    const char* proto,
    const char* comp,
    const char* desc)
{
    lpb_removed_protocol_fact fact{};
    fact.struct_size = sizeof(lpb_removed_protocol_fact);
    strncpy_s(fact.protocol_name, proto, _TRUNCATE);
    strncpy_s(fact.component, comp, _TRUNCATE);
    strncpy_s(fact.description, desc, _TRUNCATE);
    out_facts.push_back(fact);
}

static bool read_file_binary(const std::string& path, std::vector<uint8_t>& out_data) {
    auto p = utf8_to_path(path.c_str());
    std::ifstream ifs(p, std::ios::binary | std::ios::ate);
    if (!ifs.is_open()) return false;
    const auto position = ifs.tellg();
    if (position < std::streampos(0)) return false;
    const auto size = static_cast<std::streamsize>(position);
    if (size < 0 || static_cast<uint64_t>(size) > std::numeric_limits<size_t>::max()) return false;
    out_data.resize(static_cast<size_t>(size));
    ifs.seekg(0, std::ios::beg);
    ifs.read(reinterpret_cast<char*>(out_data.data()), size);
    return ifs.gcount() == size;
}

static bool write_file_binary(const std::string& path, const std::vector<uint8_t>& data) {
    auto p = utf8_to_path(path.c_str());
    std::error_code ec;
    auto temp_dir = p.parent_path();
    if (temp_dir.empty()) temp_dir = fs::current_path(ec);
    if (ec || temp_dir.empty()) return false;
    wchar_t temp_name[MAX_PATH]{};
    if (GetTempFileNameW(temp_dir.c_str(), L"lpb", 0, temp_name) == 0) return false;
    const fs::path temp(temp_name);
    // Never expose a partially written artifact. Publish only after the
    // complete temporary file has been flushed and closed.
    std::ofstream ofs(temp, std::ios::binary | std::ios::trunc);
    if (!ofs.is_open()) { fs::remove(temp, ec); return false; }
    if (!data.empty()) ofs.write(reinterpret_cast<const char*>(data.data()), static_cast<std::streamsize>(data.size()));
    ofs.flush();
    if (!ofs.good()) { ofs.close(); fs::remove(temp, ec); return false; }
    ofs.close();
    if (!MoveFileExW(temp.c_str(), p.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        fs::remove(temp, ec);
        return false;
    }
    return true;
}

static bool contains_text(const std::vector<uint8_t>& data, std::string_view value)
{
    if (value.empty() || data.size() < value.size()) return false;
    const auto* begin = data.data();
    const auto* end = begin + data.size();
    return std::search(begin, end, value.begin(), value.end()) != end;
}

// Apple MOV metadata samples remain byte-packed in mdat after their metadata
// tracks are removed.  Validation must inspect the owning moov structure, not
// search the entire file, or those now-unreferenced sample bytes look like
// live-photo metadata and cause a false cleaning failure.
static bool contains_text_in_moov(const std::vector<uint8_t>& data, std::string_view value)
{
    if (value.empty()) return false;
    const size_t moov = find_top_level_box(data, "moov");
    if (moov == std::numeric_limits<size_t>::max() || moov + 8 > data.size()) return false;
    const uint32_t moov_size = read_be32(data.data() + moov);
    if (moov_size < 8 || moov_size > data.size() - moov) return false;
    const auto begin = data.begin() + static_cast<std::ptrdiff_t>(moov);
    const auto end = begin + moov_size;
    return std::search(begin, end, value.begin(), value.end()) != end;
}

static bool remove_validated_ranges(
    lpb_context* context,
    const std::string& input_path,
    const std::string& output_path,
    const lpb_source_media_facts& facts,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    std::vector<uint8_t> data;
    if (!read_file_binary(input_path, data))
    {
        set_error(context, "Failed to read source artifact for validated range cleaning.");
        return false;
    }

    std::vector<std::pair<uint64_t, uint64_t>> ranges;
    const auto add_range = [&](uint64_t offset, uint64_t length) {
        if (length == 0) return true;
        if (offset > data.size() || length > data.size() - static_cast<size_t>(offset)) return false;
        ranges.emplace_back(offset, length);
        return true;
    };

    // These ranges were validated by Inspector. They are only applicable when
    // the extractor kept the source container in the image artifact.
    const auto range_is_in_artifact = [&](const lpb_media_range& range) {
        return range.length == 0 ||
            (range.offset <= data.size() && range.length <= data.size() - static_cast<size_t>(range.offset));
    };
    // The source facts use offsets in the original source file.  An extracted
    // primary image can itself have length == data.size(), so equality alone
    // must not be used to decide that it is still the complete source.
    const bool full_source_artifact = facts.primary_image.file_range.offset == 0 &&
        facts.primary_image.file_range.length == data.size() &&
        range_is_in_artifact(facts.motion_video.file_range) &&
        range_is_in_artifact(facts.gain_map.file_range) &&
        range_is_in_artifact(facts.protocol_tail_range);
    if (!full_source_artifact) return write_file_binary(output_path, data);

    if (facts.protocol != LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL && facts.motion_video.is_present &&
        facts.motion_video.file_range.offset > 0)
    {
        if (!add_range(facts.motion_video.file_range.offset, facts.motion_video.file_range.length))
        {
            set_error(context, "Inspector video range is outside the source artifact.");
            return false;
        }
    }
    // GainMap is an auxiliary artifact and is extracted separately. Remove its
    // embedded copy from the primary artifact only when Inspector supplied an
    // exact range; never infer it from item ordering or a string.
    if (facts.gain_map.is_present && facts.gain_map.file_range.offset > 0)
    {
        if (!add_range(facts.gain_map.file_range.offset, facts.gain_map.file_range.length))
        {
            set_error(context, "Inspector GainMap range is outside the source artifact.");
            return false;
        }
    }
    if (!add_range(facts.protocol_tail_range.offset, facts.protocol_tail_range.length))
    {
        set_error(context, "Inspector protocol-tail range is outside the source artifact.");
        return false;
    }

    if (ranges.empty()) return write_file_binary(output_path, data);
    std::sort(ranges.begin(), ranges.end());
    uint64_t previous_end = 0;
    for (const auto& range : ranges)
    {
        if (range.first < previous_end)
        {
            set_error(context, "Inspector protocol ranges overlap.");
            return false;
        }
        previous_end = range.first + range.second;
    }

    std::vector<uint8_t> cleaned;
    cleaned.reserve(data.size());
    size_t cursor = 0;
    for (const auto& range : ranges)
    {
        const size_t start = static_cast<size_t>(range.first);
        cleaned.insert(cleaned.end(), data.begin() + cursor, data.begin() + start);
        cursor = start + static_cast<size_t>(range.second);
        lpb_removed_protocol_fact fact{};
        fact.struct_size = sizeof(fact);
        strncpy_s(fact.protocol_name, "Source protocol", _TRUNCATE);
        strncpy_s(fact.component, "Validated embedded range", _TRUNCATE);
        strncpy_s(fact.description, "Removed bytes at an Inspector-validated protocol range", _TRUNCATE);
        out_facts.push_back(fact);
    }
    cleaned.insert(cleaned.end(), data.begin() + cursor, data.end());
    return write_file_binary(output_path, cleaned);
}

static lpb_result fast_file_copy(lpb_context* context, const char* in_path, const char* out_path) {
    if (!in_path || !out_path) return LPB_RESULT_INVALID_ARGUMENT;
    std::vector<uint8_t> data;
    if (!read_file_binary(in_path, data) || !write_file_binary(out_path, data)) {
        set_error(context, "Failed to copy media file atomically.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return LPB_RESULT_OK;
}

static std::string extract_xml_fragment(std::string_view sv) {
    const std::string start_tag = "<x:xmpmeta";
    const std::string end_tag = "</x:xmpmeta>";

    auto start_pos = sv.find(start_tag);
    if (start_pos != std::string_view::npos) {
        auto end_pos = sv.find(end_tag, start_pos);
        if (end_pos != std::string_view::npos) {
            return std::string(sv.substr(start_pos, end_pos + end_tag.length() - start_pos));
        }
    }

    const std::string rdf_start = "<rdf:RDF";
    const std::string rdf_end = "</rdf:RDF>";
    start_pos = sv.find(rdf_start);
    if (start_pos != std::string_view::npos) {
        auto end_pos = sv.find(rdf_end, start_pos);
        if (end_pos != std::string_view::npos) {
            return std::string(sv.substr(start_pos, end_pos + rdf_end.length() - start_pos));
        }
    }

    return {};
}

static std::string extract_xmp_string(const std::vector<uint8_t>& data) {
    if (data.size() < 2 || data[0] != 0xFF || data[1] != 0xD8) return {};
    constexpr char xmp_header[] = "http://ns.adobe.com/xap/1.0/\0";
    constexpr size_t xmp_header_size = sizeof(xmp_header) - 1;
    size_t p = 2;
    while (p + 2 <= data.size()) {
        if (data[p] != 0xFF) return {};
        while (p < data.size() && data[p] == 0xFF) ++p;
        if (p >= data.size()) return {};
        const uint8_t marker = data[p++];
        if (marker == 0xDA || marker == 0xD9) break;
        if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
        if (p + 2 > data.size()) return {};
        const size_t segment_length = (static_cast<size_t>(data[p]) << 8) | data[p + 1];
        if (segment_length < 2 || segment_length - 2 > data.size() - (p + 2)) return {};
        const size_t payload = p + 2;
        const size_t payload_size = segment_length - 2;
        if (marker == 0xE1 && payload_size >= xmp_header_size &&
            std::memcmp(data.data() + payload, xmp_header, xmp_header_size) == 0) {
            return extract_xml_fragment(std::string_view(
                reinterpret_cast<const char*>(data.data() + payload + xmp_header_size),
                payload_size - xmp_header_size));
        }
        p = payload + payload_size;
    }
    return {};
}

static lpb_result clean_jpeg_xmp(
    lpb_context* context,
    const std::string& in_path,
    const std::string& out_path,
    lpb_source_protocol protocol,
    bool require_protocol_xmp,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    std::vector<uint8_t> data;
    if (!read_file_binary(in_path, data)) {
        set_error(context, "Failed to read input JPEG for cleaning.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    std::string xmp = extract_xmp_string(data);
    if (xmp.empty()) {
        if (require_protocol_xmp) {
            set_error(context, "Expected protocol XMP was not found in the image artifact.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        return write_file_binary(out_path, data) ? LPB_RESULT_OK : LPB_RESULT_INTERNAL_ERROR;
    }

    std::string cleaned_xmp;
    std::vector<lpb_removed_protocol_fact> operation_facts;
    if (!protocols::clean::clean_xmp_metadata(xmp, protocol, cleaned_xmp, operation_facts)) {
        if (require_protocol_xmp) {
            set_error(context, "Protocol XMP was malformed or contained no validated removable fields.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        return write_file_binary(out_path, data) ? LPB_RESULT_OK : LPB_RESULT_INTERNAL_ERROR;
    }

    std::vector<uint8_t> out_buf(data.size() + cleaned_xmp.size() + 4096);
    size_t written = 0;
    lpb_result res = lpb_jpeg_inject_xmp(
        context, data.data(), data.size(),
        reinterpret_cast<const uint8_t*>(cleaned_xmp.data()), cleaned_xmp.size(),
        out_buf.data(), out_buf.size(), &written);
    if (res != LPB_RESULT_OK || written == 0) {
        set_error(context, "Failed to structurally rewrite cleaned JPEG XMP.");
        return res == LPB_RESULT_OK ? LPB_RESULT_INTERNAL_ERROR : res;
    }
    out_buf.resize(written);
    const std::string verify_xmp = extract_xmp_string(out_buf);
    std::string residual;
    std::vector<lpb_removed_protocol_fact> residual_facts;
    if (protocols::clean::clean_xmp_metadata(verify_xmp, protocol, residual, residual_facts)) {
        set_error(context, "Cleaned JPEG still contains validated Live/Motion Photo XMP fields.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    data = std::move(out_buf);
    out_facts.insert(out_facts.end(), operation_facts.begin(), operation_facts.end());

    if (!write_file_binary(out_path, data)) {
        set_error(context, "Failed to write cleaned JPEG.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    return LPB_RESULT_OK;
}

static lpb_result clean_apple_image(
    lpb_context* context,
    const std::string& in_path,
    const std::string& out_path,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    std::vector<uint8_t> data;
    if (!read_file_binary(in_path, data)) {
        set_error(context, "Failed to read Apple image for cleaning.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    const std::vector<uint8_t> before_makernote = data;
    lpb_result res = lpb_apple_strip_live_photo_entries(context, data.data(), data.size());
    if (res == LPB_RESULT_OK) {
        if (data != before_makernote) {
            add_fact(out_facts, "Apple", "MakerNote Live Tags", "Removed 0x0011/0x0017 MakerNote Live Photo tags");
        }
    } else {
        return res;
    }

    std::string xmp = extract_xmp_string(data);
    if (!xmp.empty()) {
        std::string cleaned_xmp;
        if (protocols::clean::clean_xmp_metadata(xmp, LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO, cleaned_xmp, out_facts)) {
            if (data.size() > 2 && data[0] == 0xFF && data[1] == 0xD8) {
                std::vector<uint8_t> out_buf(data.size() + cleaned_xmp.size() + 4096);
                size_t written = 0;
                const lpb_result inject_result = lpb_jpeg_inject_xmp(context, data.data(), data.size(), reinterpret_cast<const uint8_t*>(cleaned_xmp.data()), cleaned_xmp.size(), out_buf.data(), out_buf.size(), &written);
                if (inject_result != LPB_RESULT_OK || written == 0) {
                    set_error(context, "Failed to publish cleaned Apple JPEG XMP.");
                    return inject_result == LPB_RESULT_OK ? LPB_RESULT_INTERNAL_ERROR : inject_result;
                }
                if (written > 0) {
                    out_buf.resize(written);
                    data = std::move(out_buf);
                }
            }
        }
    }

    if (!write_file_binary(out_path, data)) {
        set_error(context, "Failed to write cleaned Apple image.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return LPB_RESULT_OK;
}

static lpb_result clean_apple_video(
    lpb_context* context,
    const std::string& in_path,
    const std::string& out_path,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    std::vector<uint8_t> data;
    if (!read_file_binary(in_path, data)) {
        set_error(context, "Failed to read Apple video for cleaning.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    const bool had_mdta = contains_text(data, "com.apple.quicktime.content.identifier") ||
        contains_text(data, "com.apple.quicktime.live-photo");
    const char* starts[] = {
        "com.apple.quicktime.content.identifier",
        "com.apple.quicktime.live-photo"
    };
    std::vector<uint8_t> out_a(data.size() + 4096);
    size_t written_a = 0;
    lpb_result strip_mdta = lpb_mp4_strip_mdta_keys(context, data.data(), data.size(), starts, 2, nullptr, 0, nullptr, 0, out_a.data(), out_a.size(), &written_a);
    if (strip_mdta != LPB_RESULT_OK) return strip_mdta;
    if (written_a > 0) {
        out_a.resize(written_a);
        data = std::move(out_a);
        add_fact(out_facts, "Apple", "QuickTime MDTA Keys", "Removed com.apple.quicktime.content.identifier and live-photo keys");
    }

    // Remove only Apple Live Photo metadata tracks.  A plain "mebx" match
    // would also remove unrelated QuickTime metadata such as the video
    // orientation track.
    const char* track_patterns[] = {
        "com.apple.quicktime.live-photo-info",
        "com.apple.quicktime.still-image-time",
        "com.apple.quicktime.live-photo-still-image-transform",
        "com.apple.quicktime.live-photo-still-image-transform-reference-dimensions"
    };
    std::vector<uint8_t> out_b(data.size() + 4096);
    size_t written_b = 0;
    const bool had_tracks = contains_text_in_moov(data, track_patterns[0]) ||
        contains_text_in_moov(data, track_patterns[1]) ||
        contains_text_in_moov(data, track_patterns[2]) ||
        contains_text_in_moov(data, track_patterns[3]);
    lpb_result strip_tracks = lpb_mp4_strip_stsd_tracks(
        context, data.data(), data.size(), track_patterns, 4,
        out_b.data(), out_b.size(), &written_b);
    if (strip_tracks != LPB_RESULT_OK) return strip_tracks;
    if (written_b > 0) {
        out_b.resize(written_b);
        data = std::move(out_b);
        add_fact(out_facts, "Apple", "QuickTime Live Photo Tracks", "Removed Apple Live Photo metadata tracks");
    }

    if ((had_mdta && (contains_text_in_moov(data, "com.apple.quicktime.content.identifier") ||
        contains_text_in_moov(data, "com.apple.quicktime.live-photo"))) ||
        (had_tracks && (contains_text_in_moov(data, track_patterns[0]) ||
        contains_text_in_moov(data, track_patterns[1]) ||
        contains_text_in_moov(data, track_patterns[2]) ||
        contains_text_in_moov(data, track_patterns[3])))) {
        set_error(context, "Cleaned Apple MOV still contains Live Photo metadata.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    if (!write_file_binary(out_path, data)) {
        set_error(context, "Failed to write cleaned Apple video.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return LPB_RESULT_OK;
}

static lpb_result clean_vivo_legacy_video(
    lpb_context* context,
    const std::string& in_path,
    const std::string& out_path,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    std::vector<uint8_t> data;
    if (!read_file_binary(in_path, data)) {
        set_error(context, "Failed to read vivo legacy video for cleaning.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    const uint8_t vivo_uuid[16] = {
        0x76, 0x69, 0x76, 0x6F, 0x4D, 0x65, 0x64, 0x69,
        0x61, 0x45, 0x78, 0x74, 0x49, 0x6E, 0x66, 0x6F
    };
    const bool had_vivo_uuid = contains_text(data, "vivoMediaExtInfo");
    std::vector<uint8_t> out_u(data.size() + 4096);
    size_t written_u = 0;
    lpb_result strip_uuid = lpb_mp4_strip_uuid_box(context, data.data(), data.size(), vivo_uuid, out_u.data(), out_u.size(), &written_u);
    if (strip_uuid != LPB_RESULT_OK) return strip_uuid;
    if (written_u > 0) {
        out_u.resize(written_u);
        data = std::move(out_u);
        add_fact(out_facts, "vivo", "MP4 UUID Box", "Removed vivoMediaExtInfo UUID box");
    }

    const char* starts[] = {
        "com.android.camera.livephoto",
        "com.android.camera.imageTime",
        "com.vivo.gallery.livePhoto",
        "bestTime"
    };
    std::vector<uint8_t> out_a(data.size() + 4096);
    size_t written_a = 0;
    lpb_result strip_mdta = lpb_mp4_strip_mdta_keys(context, data.data(), data.size(), starts, 4, nullptr, 0, nullptr, 0, out_a.data(), out_a.size(), &written_a);
    if (strip_mdta != LPB_RESULT_OK) return strip_mdta;
    if (written_a > 0) {
        out_a.resize(written_a);
        data = std::move(out_a);
        add_fact(out_facts, "vivo", "QuickTime MDTA Keys", "Removed com.android.camera.livephoto and livePhoto MDTA keys");
    }

    if (had_vivo_uuid && contains_text(data, "vivoMediaExtInfo")) {
        set_error(context, "Cleaned vivo video still contains its protocol UUID.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    if (!write_file_binary(out_path, data)) {
        set_error(context, "Failed to write cleaned vivo legacy video.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return LPB_RESULT_OK;
}

static lpb_result clean_huawei_video(
    lpb_context* context,
    const std::string& in_path,
    const std::string& out_path,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    std::vector<uint8_t> data;
    if (!read_file_binary(in_path, data)) {
        set_error(context, "Failed to read Huawei video for cleaning.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    const bool had_huawei_metadata = contains_text(data, "com.openharmony.movingphoto") ||
        contains_text(data, "com.huawei.movingphoto") || contains_text(data, "covertime") ||
        contains_text(data, "meta_id");
    const char* starts[] = {
        "com.openharmony.movingphoto",
        "com.huawei.movingphoto",
        "meta_id"
    };
    const char* contains[] = { "com.openharmony.covertime", "covertime" };
    std::vector<uint8_t> out_a(data.size() + 4096);
    size_t written_a = 0;
    lpb_result strip_mdta = lpb_mp4_strip_mdta_keys(context, data.data(), data.size(), starts, 3, contains, 2, nullptr, 0, out_a.data(), out_a.size(), &written_a);
    if (strip_mdta != LPB_RESULT_OK) return strip_mdta;
    if (written_a > 0) {
        out_a.resize(written_a);
        data = std::move(out_a);
        add_fact(out_facts, "Huawei", "QuickTime MDTA Keys", "Removed com.openharmony.movingphoto and covertime keys");
    }

    const char* track_patterns[] = {
        "com.openharmony.timed_metadata.movingphoto",
        "movingphoto",
        "covertime",
        "meta_id"
    };
    std::vector<uint8_t> out_b(data.size() + 4096);
    size_t written_b = 0;
    lpb_result strip_tracks = lpb_mp4_strip_stsd_tracks(
        context, data.data(), data.size(), track_patterns, 4,
        out_b.data(), out_b.size(), &written_b);
    if (strip_tracks != LPB_RESULT_OK) return strip_tracks;
    if (written_b > 0) {
        out_b.resize(written_b);
        data = std::move(out_b);
        add_fact(out_facts, "Huawei", "Moving Photo metadata track", "Removed the validated movingphoto timed-metadata track");
    }

    // meta_id is a generic vendor field and may be an ordinary value; only
    // reject residual keys whose protocol ownership was proven by Inspector.
    if (had_huawei_metadata && (contains_text(data, "com.openharmony.movingphoto") ||
        contains_text(data, "com.huawei.movingphoto") || contains_text(data, "com.openharmony.covertime"))) {
        set_error(context, contains_text(data, "com.openharmony.covertime")
            ? "Cleaned Huawei video still contains com.openharmony.covertime metadata."
            : (contains_text(data, "com.huawei.movingphoto")
                ? "Cleaned Huawei video still contains com.huawei.movingphoto metadata."
                : "Cleaned Huawei video still contains com.openharmony.movingphoto metadata."));
        return LPB_RESULT_INTERNAL_ERROR;
    }

    if (!write_file_binary(out_path, data)) {
        set_error(context, "Failed to write cleaned Huawei video.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return LPB_RESULT_OK;
}

lpb_result clean_source_protocol(
    lpb_context* context,
    const lpb_source_media_facts* facts,
    const char* input_image_path,
    const char* input_video_path,
    const char* output_image_path,
    const char* output_video_path,
    lpb_removed_protocol_fact* out_facts,
    size_t facts_capacity,
    size_t* out_facts_count)
{
    if (!context || !facts || !input_image_path || !output_image_path) {
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (facts_capacity > 0 && !out_facts) {
        set_error(context, "A facts buffer is required when facts_capacity is non-zero.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (out_facts_count) *out_facts_count = 0;

    try {
        std::vector<lpb_removed_protocol_fact> removed_facts;
        lpb_result res = LPB_RESULT_OK;

    switch (facts->protocol) {
    case LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO:
        res = clean_apple_image(context, input_image_path, output_image_path, removed_facts);
        if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
            res = clean_apple_video(context, input_video_path, output_video_path, removed_facts);
        }
        break;

    case LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1:
        res = remove_validated_ranges(context, input_image_path, output_image_path, *facts, removed_facts)
            ? LPB_RESULT_OK : LPB_RESULT_INTERNAL_ERROR;
        if (res == LPB_RESULT_OK)
            res = clean_jpeg_xmp(context, output_image_path, output_image_path,
                static_cast<lpb_source_protocol>(facts->protocol), true, removed_facts);
        if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
            res = fast_file_copy(context, input_video_path, output_video_path);
        }
        break;

    case LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2:
        res = remove_validated_ranges(context, input_image_path, output_image_path, *facts, removed_facts)
            ? LPB_RESULT_OK : LPB_RESULT_INTERNAL_ERROR;
        if (res == LPB_RESULT_OK)
            res = clean_jpeg_xmp(context, output_image_path, output_image_path,
                static_cast<lpb_source_protocol>(facts->protocol), true, removed_facts);
        if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
            res = fast_file_copy(context, input_video_path, output_video_path);
        }
        break;

    case LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO:
        res = remove_validated_ranges(context, input_image_path, output_image_path, *facts, removed_facts)
            ? LPB_RESULT_OK : LPB_RESULT_INTERNAL_ERROR;
        if (res == LPB_RESULT_OK)
            res = clean_jpeg_xmp(context, output_image_path, output_image_path,
                static_cast<lpb_source_protocol>(facts->protocol), true, removed_facts);
        if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
            res = fast_file_copy(context, input_video_path, output_video_path);
        }
        break;

    case LPB_SOURCE_PROTOCOL_VIVO_X300:
        res = remove_validated_ranges(context, input_image_path, output_image_path, *facts, removed_facts)
            ? LPB_RESULT_OK : LPB_RESULT_INTERNAL_ERROR;
        if (res == LPB_RESULT_OK)
            res = clean_jpeg_xmp(context, output_image_path, output_image_path,
                static_cast<lpb_source_protocol>(facts->protocol), true, removed_facts);
        if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
            res = fast_file_copy(context, input_video_path, output_video_path);
        }
        break;

    case LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL:
        res = remove_validated_ranges(context, input_image_path, output_image_path, *facts, removed_facts)
            ? LPB_RESULT_OK : LPB_RESULT_INTERNAL_ERROR;
        if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
            res = clean_vivo_legacy_video(context, input_video_path, output_video_path, removed_facts);
        }
        break;

    case LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG:
        res = protocols::clean::clean_samsung_sef_jpeg(context, input_image_path, output_image_path, removed_facts);
        if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
            res = fast_file_copy(context, input_video_path, output_video_path);
        }
        break;

    case LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC:
        res = protocols::clean::clean_samsung_heic(context, input_image_path, output_image_path, removed_facts);
        if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
            res = fast_file_copy(context, input_video_path, output_video_path);
        }
        break;

    case LPB_SOURCE_PROTOCOL_HUAWEI_MOVING_PHOTO:
    case LPB_SOURCE_PROTOCOL_HONOR_MOVING_PHOTO:
        res = remove_validated_ranges(context, input_image_path, output_image_path, *facts, removed_facts)
            ? LPB_RESULT_OK : LPB_RESULT_INTERNAL_ERROR;
        if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
            res = clean_huawei_video(context, input_video_path, output_video_path, removed_facts);
        }
        break;

    case LPB_SOURCE_PROTOCOL_NON_LIVE:
    default:
        res = fast_file_copy(context, input_image_path, output_image_path);
        if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
            res = fast_file_copy(context, input_video_path, output_video_path);
        }
        break;
    }

        if (res == LPB_RESULT_OK && removed_facts.size() > facts_capacity) {
            if (out_facts_count) *out_facts_count = removed_facts.size();
            set_error(context, "The supplied protocol-fact buffer is too small.");
            return LPB_RESULT_BUFFER_TOO_SMALL;
        }
        if (res == LPB_RESULT_OK && out_facts && facts_capacity > 0) {
            for (size_t i = 0; i < removed_facts.size(); i++) {
                out_facts[i] = removed_facts[i];
            }
            if (out_facts_count) *out_facts_count = removed_facts.size();
        }
        return res;
    }
    catch (const std::exception& ex) {
        set_error(context, ex.what());
        return LPB_RESULT_INTERNAL_ERROR;
    }
    catch (...) {
        set_error(context, "Unhandled native exception during source protocol cleaning.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
}

} // namespace lpb::media
