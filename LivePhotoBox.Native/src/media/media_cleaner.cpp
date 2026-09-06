#include "media/media_cleaner.h"
#include "protocols/clean/xmp_cleaner.h"
#include "protocols/clean/samsung_sef_cleaner.h"
#include "protocols/clean/heif_cleaner.h"
#include "protocols/clean/jpeg_structure_cleaner.h"
#include "protocols/apple.h"
#include "foundation/residue_fingerprint.h"
#include "foundation/internal.h"
#include "binary/binary_io.h"
#include "metadata/jpeg.h"
#include "containers/isobmff.h"
#include "containers/mp4_strip.h"

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
    const char* desc,
    const char* residue_id = "",
    lpb_media_artifact_kind role = LPB_ARTIFACT_PRIMARY_IMAGE,
    lpb_residue_structure_kind structure_kind = LPB_RESIDUE_XMP_PROPERTY,
    const char* op = "Removed",
    const char* after = "Removed",
    const char* before_fp = "")
{
    lpb_removed_protocol_fact fact{};
    fact.struct_size = sizeof(lpb_removed_protocol_fact);
    strncpy_s(fact.protocol_name, proto ? proto : "", _TRUNCATE);
    strncpy_s(fact.component, comp ? comp : "", _TRUNCATE);
    strncpy_s(fact.description, desc ? desc : "", _TRUNCATE);
    strncpy_s(fact.residue_id, residue_id ? residue_id : "", _TRUNCATE);
    fact.artifact_role = role;
    fact.structure_kind = structure_kind;
    strncpy_s(fact.operation, op ? op : "Removed", _TRUNCATE);
    strncpy_s(fact.after_status, after ? after : "Removed", _TRUNCATE);
    if (before_fp) strncpy_s(fact.before_fingerprint, before_fp, _TRUNCATE);
    out_facts.push_back(fact);
}

const lpb_cleanup_action* find_authorized_action(
    const lpb_cleanup_action* actions,
    size_t action_count,
    std::string_view residue_id,
    lpb_media_artifact_kind expected_role,
    lpb_residue_structure_kind expected_kind,
    std::string_view expected_selector,
    int32_t expected_removal_mode)
{
    if (!actions || action_count == 0 || residue_id.empty()) return nullptr;
    for (size_t i = 0; i < action_count; ++i) {
        const auto& a = actions[i];
        if (residue_id == a.residue_id) {
            if (a.artifact_role != expected_role) return nullptr;
            if (a.structure_kind != expected_kind) return nullptr;
            if (!expected_selector.empty() && expected_selector != a.selector) return nullptr;
            if (expected_removal_mode >= 0 && a.removal_mode != expected_removal_mode) return nullptr;
            return &a;
        }
    }
    return nullptr;
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



static lpb_result fast_file_copy(lpb_context* context, const char* in_path, const char* out_path) {
    if (!in_path || !out_path) return LPB_RESULT_INVALID_ARGUMENT;
    auto p_in = utf8_to_path(in_path);
    auto p_out = utf8_to_path(out_path);
    std::error_code ec;

    auto temp_dir = p_out.parent_path();
    if (temp_dir.empty()) temp_dir = fs::current_path(ec);
    if (ec || temp_dir.empty()) {
        set_error(context, "Invalid output directory for copy.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    wchar_t temp_name[MAX_PATH]{};
    if (GetTempFileNameW(temp_dir.c_str(), L"lpb", 0, temp_name) == 0) {
        set_error(context, "Failed to allocate temporary copy file.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    const fs::path temp(temp_name);

    std::ifstream src(p_in, std::ios::binary);
    if (!src.is_open()) {
        fs::remove(temp, ec);
        set_error(context, "Failed to open source file for copy.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::ofstream dst(temp, std::ios::binary | std::ios::trunc);
    if (!dst.is_open()) {
        fs::remove(temp, ec);
        set_error(context, "Failed to open destination temp file for copy.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    constexpr size_t buffer_size = 1024 * 1024; // 1MB streaming buffer (bounded memory)
    std::vector<char> buffer(buffer_size);
    while (src.read(buffer.data(), buffer_size) || src.gcount() > 0) {
        dst.write(buffer.data(), src.gcount());
        if (!dst.good()) {
            dst.close();
            fs::remove(temp, ec);
            set_error(context, "Failed writing copy stream.");
            return LPB_RESULT_INTERNAL_ERROR;
        }
    }
    dst.flush();
    if (!dst.good()) {
        dst.close();
        fs::remove(temp, ec);
        set_error(context, "Failed flushing copy stream.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    dst.close();
    src.close();

    if (!MoveFileExW(temp.c_str(), p_out.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        fs::remove(temp, ec);
        set_error(context, "Failed to publish copied file atomically.");
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
    const lpb_cleanup_action* actions,
    size_t action_count,
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
    if (!protocols::clean::clean_xmp_metadata_with_plan(xmp, protocol, actions, action_count, cleaned_xmp, operation_facts)) {
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
    if (protocols::clean::clean_xmp_metadata_with_plan(verify_xmp, protocol, actions, action_count, residual, residual_facts)) {
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
    const lpb_cleanup_action* actions,
    size_t action_count,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    std::vector<uint8_t> data;
    if (!read_file_binary(in_path, data)) {
        set_error(context, "Failed to read Apple image for cleaning.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    lpb_image_container img_cont = (data.size() >= 2 && data[0] == 0xFF && data[1] == 0xD8) ? LPB_IMAGE_CONTAINER_JPEG : LPB_IMAGE_CONTAINER_HEIC;
    std::vector<uint16_t> authorized_mn_tags;
    std::string fp_0011, fp_0017, fp_0025, fp_002b;
    const auto* act_0011 = find_authorized_action(actions, action_count, "apple-img-makernote-0011",
        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_EXIF_MAKERNOTE_TAG, "0x0011", LPB_REMOVAL_REBUILD_CONTAINER);
    const auto* act_0017 = find_authorized_action(actions, action_count, "apple-img-makernote-0017",
        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_EXIF_MAKERNOTE_TAG, "0x0017", LPB_REMOVAL_REBUILD_CONTAINER);
    const auto* act_0025 = find_authorized_action(actions, action_count, "apple-img-makernote-0025",
        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_EXIF_MAKERNOTE_TAG, "0x0025", LPB_REMOVAL_REBUILD_CONTAINER);
    const auto* act_002b = find_authorized_action(actions, action_count, "apple-img-makernote-002b",
        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_EXIF_MAKERNOTE_TAG, "0x002b", LPB_REMOVAL_REBUILD_CONTAINER);

    if (act_0011) {
        if (lpb::protocols::apple::apple_image_get_tag_fingerprint(context, data, img_cont, 0x0011, fp_0011)) {
            if (act_0011->expected_fingerprint[0] != '\0' && fp_0011 != act_0011->expected_fingerprint) {
                set_error(context, "Residue fingerprint mismatch for apple-img-makernote-0011.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            authorized_mn_tags.push_back(0x0011);
        }
    }
    if (act_0017) {
        if (lpb::protocols::apple::apple_image_get_tag_fingerprint(context, data, img_cont, 0x0017, fp_0017)) {
            if (act_0017->expected_fingerprint[0] != '\0' && fp_0017 != act_0017->expected_fingerprint) {
                set_error(context, "Residue fingerprint mismatch for apple-img-makernote-0017.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            authorized_mn_tags.push_back(0x0017);
        }
    }
    if (act_0025) {
        if (lpb::protocols::apple::apple_image_get_tag_fingerprint(context, data, img_cont, 0x0025, fp_0025)) {
            if (act_0025->expected_fingerprint[0] != '\0' && fp_0025 != act_0025->expected_fingerprint) {
                set_error(context, "Residue fingerprint mismatch for apple-img-makernote-0025.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            authorized_mn_tags.push_back(0x0025);
        }
    }
    if (act_002b) {
        if (lpb::protocols::apple::apple_image_get_tag_fingerprint(context, data, img_cont, 0x002b, fp_002b)) {
            if (act_002b->expected_fingerprint[0] != '\0' && fp_002b != act_002b->expected_fingerprint) {
                set_error(context, "Residue fingerprint mismatch for apple-img-makernote-002b.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            authorized_mn_tags.push_back(0x002b);
        }
    }

    if (!authorized_mn_tags.empty()) {
        uint16_t stripped_tags[4]{};
        size_t stripped_count = 0;
        lpb_result res = lpb_apple_strip_live_photo_entries_selective(
            context, data.data(), data.size(),
            authorized_mn_tags.data(), authorized_mn_tags.size(),
            stripped_tags, 4, &stripped_count);
        if (res != LPB_RESULT_OK) {
            return res;
        }

        for (size_t i = 0; i < stripped_count; ++i) {
            switch (stripped_tags[i]) {
            case 0x0011:
                add_fact(out_facts, "Apple", "MakerNote Live Tags", "Removed 0x0011 MakerNote Live Photo tag",
                    "apple-img-makernote-0011", LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_EXIF_MAKERNOTE_TAG,
                    "Removed", "Removed", fp_0011.c_str());
                break;
            case 0x0017:
                add_fact(out_facts, "Apple", "MakerNote Live Tags", "Removed 0x0017 MakerNote Live Photo tag",
                    "apple-img-makernote-0017", LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_EXIF_MAKERNOTE_TAG,
                    "Removed", "Removed", fp_0017.c_str());
                break;
            case 0x0025:
                add_fact(out_facts, "Apple", "MakerNote Live Tags", "Removed 0x0025 MakerNote Live Photo tag",
                    "apple-img-makernote-0025", LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_EXIF_MAKERNOTE_TAG,
                    "Removed", "Removed", fp_0025.c_str());
                break;
            case 0x002b:
                add_fact(out_facts, "Apple", "MakerNote Live Tags", "Removed 0x002b MakerNote Live Photo tag",
                    "apple-img-makernote-002b", LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_EXIF_MAKERNOTE_TAG,
                    "Removed", "Removed", fp_002b.c_str());
                break;
            }
        }
    }

    std::string xmp = extract_xmp_string(data);
    if (!xmp.empty()) {
        std::string cleaned_xmp;
        if (protocols::clean::clean_xmp_metadata_with_plan(xmp, LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO, actions, action_count, cleaned_xmp, out_facts)) {
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
    const lpb_cleanup_action* actions,
    size_t action_count,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    const bool should_strip_cid = find_authorized_action(actions, action_count, "apple-vid-mdta-cid",
        LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.apple.quicktime.content.identifier", LPB_REMOVAL_DELETE) != nullptr;
    const bool should_strip_livephoto = find_authorized_action(actions, action_count, "apple-vid-mdta-livephoto",
        LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.apple.quicktime.live-photo", LPB_REMOVAL_DELETE) != nullptr;

    std::vector<const char*> track_patterns;
    std::vector<std::pair<const char*, const char*>> track_residues;
    if (find_authorized_action(actions, action_count, "apple-vid-track-livephoto-info",
            LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_METADATA_TRACK, "com.apple.quicktime.live-photo-info", LPB_REMOVAL_DELETE)) {
        track_patterns.push_back("com.apple.quicktime.live-photo-info");
        track_residues.push_back({"apple-vid-track-livephoto-info", "Removed com.apple.quicktime.live-photo-info track"});
    }
    if (find_authorized_action(actions, action_count, "apple-vid-track-still-image-time",
            LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_METADATA_TRACK, "com.apple.quicktime.still-image-time", LPB_REMOVAL_DELETE)) {
        track_patterns.push_back("com.apple.quicktime.still-image-time");
        track_residues.push_back({"apple-vid-track-still-image-time", "Removed com.apple.quicktime.still-image-time track"});
    }
    if (find_authorized_action(actions, action_count, "apple-vid-track-transform",
            LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_METADATA_TRACK, "com.apple.quicktime.live-photo-still-image-transform", LPB_REMOVAL_DELETE)) {
        track_patterns.push_back("com.apple.quicktime.live-photo-still-image-transform");
        track_residues.push_back({"apple-vid-track-transform", "Removed com.apple.quicktime.live-photo-still-image-transform track"});
    }
    if (find_authorized_action(actions, action_count, "apple-vid-track-reference-dimensions",
            LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_METADATA_TRACK, "com.apple.quicktime.live-photo-still-image-transform-reference-dimensions", LPB_REMOVAL_DELETE)) {
        track_patterns.push_back("com.apple.quicktime.live-photo-still-image-transform-reference-dimensions");
        track_residues.push_back({"apple-vid-track-reference-dimensions", "Removed com.apple.quicktime.live-photo-still-image-transform-reference-dimensions track"});
    }

    if (!should_strip_cid && !should_strip_livephoto && track_patterns.empty()) {
        return fast_file_copy(context, in_path.c_str(), out_path.c_str());
    }

    std::vector<const char*> starts;
    std::vector<std::pair<const char*, const char*>> mdta_residues;
    if (should_strip_cid) {
        starts.push_back("com.apple.quicktime.content.identifier");
        mdta_residues.push_back({"apple-vid-mdta-cid", "Removed com.apple.quicktime.content.identifier key"});
    }
    if (should_strip_livephoto) {
        starts.push_back("com.apple.quicktime.live-photo");
        mdta_residues.push_back({"apple-vid-mdta-livephoto", "Removed com.apple.quicktime.live-photo key"});
    }

    lpb::containers::Mp4StripSpec spec{};
    spec.mdta_starts = starts.data();
    spec.mdta_starts_count = starts.size();
    spec.track_patterns = track_patterns.data();
    spec.track_patterns_count = track_patterns.size();
    spec.actions = actions;
    spec.action_count = action_count;

    lpb::containers::Mp4StripOutcome outcome{};
    lpb_result res = lpb::containers::stream_clean_mp4_file(context, in_path, out_path, spec, outcome);
    if (res != LPB_RESULT_OK) return res;

    if (outcome.mdta_removed) {
        for (size_t i = 0; i < mdta_residues.size(); ++i) {
            if (i < outcome.mdta_starts_matched.size() && outcome.mdta_starts_matched[i]) {
                const char* fp = i < outcome.mdta_starts_fingerprints.size() ? outcome.mdta_starts_fingerprints[i].c_str() : "";
                add_fact(out_facts, "Apple", "QuickTime MDTA Keys", mdta_residues[i].second,
                    mdta_residues[i].first, LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "Removed", "Removed", fp);
            }
        }
    }
    if (outcome.track_removed) {
        for (size_t i = 0; i < track_residues.size(); ++i) {
            if (i < outcome.track_patterns_matched.size() && outcome.track_patterns_matched[i]) {
                const char* fp = i < outcome.track_fingerprints.size() ? outcome.track_fingerprints[i].c_str() : "";
                add_fact(out_facts, "Apple", "QuickTime Live Photo Tracks", track_residues[i].second,
                    track_residues[i].first, LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_METADATA_TRACK, "Removed", "Removed", fp);
            }
        }
    }
    return LPB_RESULT_OK;
}

static lpb_result clean_vivo_legacy_video(
    lpb_context* context,
    const std::string& in_path,
    const std::string& out_path,
    const lpb_cleanup_action* actions,
    size_t action_count,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    const auto* act_uuid = find_authorized_action(actions, action_count, "vivo-legacy-vid-uuid",
        LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_UUID_BOX, "vivoMediaExtInfo", LPB_REMOVAL_DELETE);
    const auto* act_lp = find_authorized_action(actions, action_count, "vivo-legacy-vid-mdta-livephoto",
        LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.android.camera.livephoto", LPB_REMOVAL_DELETE);
    const auto* act_it = find_authorized_action(actions, action_count, "vivo-legacy-vid-mdta-imagetime",
        LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.android.camera.imageTime", LPB_REMOVAL_DELETE);
    const auto* act_gallery = find_authorized_action(actions, action_count, "vivo-legacy-vid-mdta-gallery",
        LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.vivo.gallery.livePhoto", LPB_REMOVAL_DELETE);

    if (!act_uuid && !act_lp && !act_it && !act_gallery) {
        return fast_file_copy(context, in_path.c_str(), out_path.c_str());
    }

    const uint8_t vivo_uuid[16] = {
        0x76, 0x69, 0x76, 0x6F, 0x4D, 0x65, 0x64, 0x69,
        0x61, 0x45, 0x78, 0x74, 0x49, 0x6E, 0x66, 0x6F
    };

    std::vector<const char*> starts;
    std::vector<std::pair<const char*, const char*>> mdta_residues;
    if (act_lp) {
        starts.push_back("com.android.camera.livephoto");
        mdta_residues.push_back({"vivo-legacy-vid-mdta-livephoto", "Removed com.android.camera.livephoto MDTA key"});
    }
    if (act_it) {
        starts.push_back("com.android.camera.imageTime");
        mdta_residues.push_back({"vivo-legacy-vid-mdta-imagetime", "Removed com.android.camera.imageTime MDTA key"});
    }
    if (act_gallery) {
        starts.push_back("com.vivo.gallery.livePhoto");
        mdta_residues.push_back({"vivo-legacy-vid-mdta-gallery", "Removed com.vivo.gallery.livePhoto MDTA key"});
    }

    lpb::containers::Mp4StripSpec spec{};
    if (act_uuid) spec.strip_uuid_16 = vivo_uuid;
    spec.mdta_starts = starts.data();
    spec.mdta_starts_count = starts.size();
    spec.actions = actions;
    spec.action_count = action_count;

    lpb::containers::Mp4StripOutcome outcome{};
    lpb_result res = lpb::containers::stream_clean_mp4_file(context, in_path, out_path, spec, outcome);
    if (res != LPB_RESULT_OK) return res;

    if (outcome.uuid_removed && act_uuid) {
        add_fact(out_facts, "vivo", "MP4 UUID Box", "Removed vivoMediaExtInfo UUID box",
            "vivo-legacy-vid-uuid", LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_UUID_BOX, "Removed", "Removed",
            outcome.uuid_fingerprint.c_str());
    }
    if (outcome.mdta_removed) {
        for (size_t i = 0; i < mdta_residues.size(); ++i) {
            if (i < outcome.mdta_starts_matched.size() && outcome.mdta_starts_matched[i]) {
                const char* fp = i < outcome.mdta_starts_fingerprints.size() ? outcome.mdta_starts_fingerprints[i].c_str() : "";
                add_fact(out_facts, "vivo", "QuickTime MDTA Keys", mdta_residues[i].second,
                    mdta_residues[i].first, LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "Removed", "Removed", fp);
            }
        }
    }
    return LPB_RESULT_OK;
}

static lpb_result clean_huawei_video(
    lpb_context* context,
    const std::string& in_path,
    const std::string& out_path,
    const lpb_cleanup_action* actions,
    size_t action_count,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    const auto* act_openharmony = find_authorized_action(actions, action_count, "huawei-vid-mdta-openharmony",
        LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.openharmony.movingphoto", LPB_REMOVAL_DELETE);
    const auto* act_huawei = find_authorized_action(actions, action_count, "huawei-vid-mdta-huawei",
        LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.huawei.movingphoto", LPB_REMOVAL_DELETE);
    const auto* act_covertime = find_authorized_action(actions, action_count, "huawei-vid-mdta-covertime",
        LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.openharmony.covertime", LPB_REMOVAL_DELETE);
    const auto* act_track = find_authorized_action(actions, action_count, "huawei-vid-track-movingphoto",
        LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_METADATA_TRACK, "com.openharmony.timed_metadata.movingphoto", LPB_REMOVAL_DELETE);

    if (!act_openharmony && !act_huawei && !act_covertime && !act_track) {
        return fast_file_copy(context, in_path.c_str(), out_path.c_str());
    }

    std::vector<const char*> starts;
    std::vector<std::pair<const char*, const char*>> starts_residues;
    if (act_openharmony) {
        starts.push_back("com.openharmony.movingphoto");
        starts_residues.push_back({"huawei-vid-mdta-openharmony", "Removed com.openharmony.movingphoto key"});
    }
    if (act_huawei) {
        starts.push_back("com.huawei.movingphoto");
        starts_residues.push_back({"huawei-vid-mdta-huawei", "Removed com.huawei.movingphoto key"});
    }

    std::vector<const char*> contains;
    std::vector<std::pair<const char*, const char*>> contains_residues;
    if (act_covertime) {
        contains.push_back("com.openharmony.covertime");
        contains_residues.push_back({"huawei-vid-mdta-covertime", "Removed com.openharmony.covertime key"});
    }

    std::vector<const char*> track_patterns;
    if (act_track) {
        track_patterns.push_back("com.openharmony.timed_metadata.movingphoto");
    }

    lpb::containers::Mp4StripSpec spec{};
    spec.mdta_starts = starts.data();
    spec.mdta_starts_count = starts.size();
    spec.mdta_contains = contains.data();
    spec.mdta_contains_count = contains.size();
    spec.track_patterns = track_patterns.data();
    spec.track_patterns_count = track_patterns.size();
    spec.actions = actions;
    spec.action_count = action_count;

    lpb::containers::Mp4StripOutcome outcome{};
    lpb_result res = lpb::containers::stream_clean_mp4_file(context, in_path, out_path, spec, outcome);
    if (res != LPB_RESULT_OK) return res;

    if (outcome.mdta_removed) {
        for (size_t i = 0; i < starts_residues.size(); ++i) {
            if (i < outcome.mdta_starts_matched.size() && outcome.mdta_starts_matched[i]) {
                const char* fp = i < outcome.mdta_starts_fingerprints.size() ? outcome.mdta_starts_fingerprints[i].c_str() : "";
                add_fact(out_facts, "Huawei", "QuickTime MDTA Keys", starts_residues[i].second,
                    starts_residues[i].first, LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "Removed", "Removed", fp);
            }
        }
        for (size_t i = 0; i < contains_residues.size(); ++i) {
            if (i < outcome.mdta_contains_matched.size() && outcome.mdta_contains_matched[i]) {
                const char* fp = i < outcome.mdta_contains_fingerprints.size() ? outcome.mdta_contains_fingerprints[i].c_str() : "";
                add_fact(out_facts, "Huawei", "QuickTime MDTA Keys", contains_residues[i].second,
                    contains_residues[i].first, LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "Removed", "Removed", fp);
            }
        }
    }
    if (outcome.track_removed && act_track) {
        if (!outcome.track_patterns_matched.empty() && outcome.track_patterns_matched[0]) {
            const char* fp = !outcome.track_fingerprints.empty() ? outcome.track_fingerprints[0].c_str() : "";
            add_fact(out_facts, "Huawei", "Moving Photo metadata track", "Removed the validated movingphoto timed-metadata track",
                "huawei-vid-track-movingphoto", LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_METADATA_TRACK, "Removed", "Removed", fp);
        }
    }
    return LPB_RESULT_OK;
}

lpb_result clean_source_protocol_with_plan(
    lpb_context* context,
    const lpb_source_media_facts* facts,
    const lpb_cleanup_action* actions,
    size_t action_count,
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
    if (!actions || action_count == 0) {
        set_error(context, "No cleanup actions authorized in plan.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (paths_alias(input_image_path, output_image_path) ||
        (input_video_path && output_image_path && paths_alias(input_video_path, output_image_path)) ||
        (input_image_path && output_video_path && paths_alias(input_image_path, output_video_path)) ||
        (input_video_path && output_video_path && paths_alias(input_video_path, output_video_path)) ||
        (output_image_path && output_video_path && paths_alias(output_image_path, output_video_path))) {
        set_error(context, "Cleaning outputs must not overwrite source files or each other.");
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
            res = clean_apple_image(context, input_image_path, output_image_path, actions, action_count, removed_facts);
            if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
                res = clean_apple_video(context, input_video_path, output_video_path, actions, action_count, removed_facts);
            }
            break;

        case LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1:
        case LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2:
        case LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO:
        case LPB_SOURCE_PROTOCOL_VIVO_X300:
            res = clean_jpeg_xmp(context, input_image_path, output_image_path,
                static_cast<lpb_source_protocol>(facts->protocol), actions, action_count, true, removed_facts);
            if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
                res = fast_file_copy(context, input_video_path, output_video_path);
            }
            break;

        case LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL:
            res = fast_file_copy(context, input_image_path, output_image_path);
            if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
                res = clean_vivo_legacy_video(context, input_video_path, output_video_path, actions, action_count, removed_facts);
            }
            break;

        case LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG:
            res = protocols::clean::clean_samsung_sef_jpeg(context, input_image_path, output_image_path, actions, action_count, removed_facts);
            if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
                res = fast_file_copy(context, input_video_path, output_video_path);
            }
            break;

        case LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC:
            res = protocols::clean::clean_samsung_heic(context, input_image_path, output_image_path, actions, action_count, removed_facts);
            if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
                res = fast_file_copy(context, input_video_path, output_video_path);
            }
            break;

        case LPB_SOURCE_PROTOCOL_HUAWEI_MOVING_PHOTO:
        case LPB_SOURCE_PROTOCOL_HONOR_MOVING_PHOTO:
            res = fast_file_copy(context, input_image_path, output_image_path);
            if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
                res = clean_huawei_video(context, input_video_path, output_video_path, actions, action_count, removed_facts);
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
            std::error_code ec;
            fs::remove(utf8_to_path(output_image_path), ec);
            if (output_video_path) fs::remove(utf8_to_path(output_video_path), ec);
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

lpb_result clean_source_protocol(
    lpb_context* context,
    const lpb_source_media_facts* /*facts*/,
    const char* /*input_image_path*/,
    const char* /*input_video_path*/,
    const char* /*output_image_path*/,
    const char* /*output_video_path*/,
    lpb_removed_protocol_fact* /*out_facts*/,
    size_t /*facts_capacity*/,
    size_t* out_facts_count)
{
    if (out_facts_count) *out_facts_count = 0;
    set_error(context, "Unplanned source protocol cleaning is forbidden. A CleanupPlan with authorized actions must be provided via lpb_clean_source_protocol_with_plan.");
    return LPB_RESULT_INVALID_ARGUMENT;
}

} // namespace lpb::media
