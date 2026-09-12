#include "media/media_cleaner.h"
#include "protocols/clean/xmp_cleaner.h"
#include "protocols/clean/samsung_sef_cleaner.h"
#include "protocols/clean/heif_cleaner.h"
#include "protocols/clean/jpeg_structure_cleaner.h"
#include "protocols/apple.h"
#include "foundation/residue_fingerprint.h"
#include "foundation/sha256.h"
#include "foundation/internal.h"
#include "binary/binary_io.h"
#include "metadata/jpeg.h"
#include "containers/isobmff.h"
#include "containers/mp4_strip.h"

#include <fstream>
#include <filesystem>
#include <vector>
#include <span>
#include <string>
#include <cstring>
#include <limits>
#include <random>
#include <string_view>
#include <unordered_set>
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>

namespace fs = std::filesystem;

namespace lpb::media {

/* RAII capability gate: low-level in-place destructive primitives are only
 * callable while a plan-authorized cleaner operation is active on the
 * context. */
/* RAII capability gate: low-level in-place destructive primitives are only
 * callable while a plan-authorized cleaner operation is active on the current
 * thread.  Authority is bound to this thread's invocation AND to an
 * unforgeable per-invocation token that is mirrored in the context: another
 * thread can never borrow the window, and a same-thread re-entrant external
 * callback (e.g. a cancellation callback that itself calls a raw writer) is
 * blocked because every external callback boundary suspends the binding first
 * (see cleaner_authority_suspend). */
struct cleaner_authority_guard
{
    lpb_clean_authority_binding previous;
    lpb_context* context;
    uint64_t token;

    explicit cleaner_authority_guard(lpb_context* c) noexcept
        : previous(tls_cleanup_authority), context(c), token(next_authority_token())
    {
        tls_cleanup_authority = {static_cast<const void*>(c), token};
        if (c) c->active_clean_authority_token = token;
    }
    ~cleaner_authority_guard() noexcept
    {
        if (context && context->active_clean_authority_token == token)
        {
            context->active_clean_authority_token = 0;
        }
        tls_cleanup_authority = previous;
    }

private:
    static uint64_t next_authority_token() noexcept
    {
        static std::atomic<uint64_t> counter{1};
        static const uint64_t seed = ([]() noexcept {
            std::random_device rd;
            return (static_cast<uint64_t>(rd()) << 32) ^ static_cast<uint64_t>(rd());
        })();
        return counter.fetch_add(1, std::memory_order_relaxed) ^ seed;
    }
};

/* Temporarily removes the thread-local authority binding while the cleaner
 * calls OUT to an external callback (cancellation checks, the post-snapshot
 * test hook, ...).  A re-entrant call into a raw destructive primitive from
 * inside that callback therefore fails the authority gate instead of borrowing
 * the cleaner's own capability window. */
struct cleaner_authority_suspend
{
    lpb_clean_authority_binding previous;
    lpb_context* context;
    uint64_t token;

    explicit cleaner_authority_suspend(lpb_context* c) noexcept
        : previous(tls_cleanup_authority), context(c), token(c ? c->active_clean_authority_token : 0)
    {
        tls_cleanup_authority = {};
        if (c) c->active_clean_authority_token = 0;
    }
    ~cleaner_authority_suspend() noexcept
    {
        if (context) context->active_clean_authority_token = token;
        tls_cleanup_authority = previous;
    }
};

/* Cancellation probe that never runs the external callback while the raw
 * writer capability is live on this thread. */
static bool cancellation_requested_suspended(lpb_context* context) noexcept
{
    cleaner_authority_suspend suspend(context);
    return lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED;
}

static bool is_all_zeroes_32(const uint8_t* hash) noexcept {
    if (!hash) return true;
    for (size_t i = 0; i < 32; ++i) {
        if (hash[i] != 0) return false;
    }
    return true;
}

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
    lpb_source_protocol expected_protocol,
    std::string_view residue_id,
    lpb_media_artifact_kind expected_role,
    lpb_residue_structure_kind expected_kind,
    std::string_view expected_selector,
    std::string_view expected_semantic,
    int32_t expected_removal_mode,
    int32_t expected_coordinate_space)
{
    if (!actions || action_count == 0 || residue_id.empty()) return nullptr;
    for (size_t i = 0; i < action_count; ++i) {
        const auto& a = actions[i];
        if (residue_id == a.residue_id) {
            if (a.owner_protocol != expected_protocol) return nullptr;
            if (a.artifact_role != expected_role) return nullptr;
            if (a.structure_kind != expected_kind) return nullptr;
            if (!expected_selector.empty() && expected_selector != a.selector) return nullptr;
            if (!expected_semantic.empty() && expected_semantic != a.expected_semantic) return nullptr;
            if (expected_removal_mode >= 0 && a.removal_mode != expected_removal_mode) return nullptr;
            if (expected_coordinate_space >= 0 && a.coordinate_space != expected_coordinate_space) return nullptr;
            if (a.expected_fingerprint[0] == '\0') return nullptr;
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

static bool read_file_binary_handle(void* file_handle, std::vector<uint8_t>& out_data) {
    const HANDLE h = static_cast<HANDLE>(file_handle);
    if (h == nullptr || h == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(h, &size) || size.QuadPart < 0) return false;
    try
    {
        out_data.resize(static_cast<size_t>(size.QuadPart));
    }
    catch (...)
    {
        return false;
    }
    if (out_data.empty()) return true;
    DWORD total = 0;
    while (total < out_data.size()) {
        DWORD chunk = 0;
        const DWORD wanted = static_cast<DWORD>(std::min<size_t>(out_data.size() - total, 32ull * 1024ull * 1024ull));
        if (!ReadFile(h, out_data.data() + total, wanted, &chunk, nullptr) || chunk == 0) return false;
        total += chunk;
    }
    return true;
}

static bool write_file_binary(lpb_context* context, int32_t artifact_role,
    const std::string& path, const std::vector<uint8_t>& data)
{
    if (context == nullptr) return false;
    auto p = utf8_to_path(path.c_str());
    std::error_code ec;
    auto temp_dir = p.parent_path();
    if (temp_dir.empty()) temp_dir = fs::current_path(ec);
    if (ec || temp_dir.empty()) return false;

    // CREATE_NEW temp acquisition: the returned handle is the FIRST creation
    // of the object (kernel-atomic name claim), so ownership starts at the
    // object's first moment of existence.  Never GetTempFileNameW +
    // OPEN_EXISTING, which re-opens a pathname after Windows created and
    // closed the file itself.
    fs::path temp;
    HANDLE temp_handle = lpb_create_unique_temp_file(temp_dir, L"lpb", temp);
    if (temp_handle == INVALID_HANDLE_VALUE)
    {
        set_error(context, "Failed to create a unique temp file for cleaned output.");
        return false;
    }

    bool ok = false;
    size_t total = 0;
    while (total < data.size())
    {
        DWORD chunk = 0;
        const DWORD wanted = static_cast<DWORD>(std::min<size_t>(data.size() - total, 32ull * 1024ull * 1024ull));
        if (!WriteFile(temp_handle, data.data() + total, wanted, &chunk, nullptr) || chunk == 0)
        {
            break;
        }
        total += chunk;
    }
    if (total == data.size() && FlushFileBuffers(temp_handle))
    {
        // Handle-based no-overwrite publish: the object this handle created is
        // renamed through the handle (FileRenameInfo, ReplaceIfExists = FALSE).
        // A foreign destination object fails closed; the source object is
        // decided by the handle, never by the temp pathname.
        ok = lpb_publish_cleaner_output_handle(context, artifact_role, temp_handle, temp, p, path);
    }
    else
    {
        set_error(context, "Failed to write or flush cleaned output through the owning handle.");
    }
    if (!ok)
    {
        // Exact-object failure cleanup THROUGH the creating handle.  Never
        // CloseHandle then fs::remove(path): a foreign object that took over
        // the temp pathname must not be deleted.
        FILE_DISPOSITION_INFO disp{};
        disp.DeleteFile = TRUE;
        static_cast<void>(SetFileInformationByHandle(temp_handle, FileDispositionInfo, &disp, sizeof(disp)));
    }
    CloseHandle(temp_handle);
    return ok;
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
    const std::vector<uint8_t>& in_bytes,
    const std::string& out_path,
    lpb_source_protocol protocol,
    const lpb_cleanup_action* actions,
    size_t action_count,
    bool require_protocol_xmp,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    std::vector<uint8_t> data = in_bytes;

    std::string xmp = extract_xmp_string(data);
    if (xmp.empty()) {
        if (require_protocol_xmp) {
            set_error(context, "Expected protocol XMP was not found in the image artifact.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        return write_file_binary(context, LPB_ARTIFACT_PRIMARY_IMAGE, out_path, data) ? LPB_RESULT_OK : LPB_RESULT_INTERNAL_ERROR;
    }

    std::string cleaned_xmp;
    std::vector<lpb_removed_protocol_fact> operation_facts;
    if (!protocols::clean::clean_xmp_metadata_with_plan(xmp, protocol, actions, action_count, cleaned_xmp, operation_facts)) {
        if (require_protocol_xmp) {
            set_error(context, "Protocol XMP was malformed or contained no validated removable fields.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        return write_file_binary(context, LPB_ARTIFACT_PRIMARY_IMAGE, out_path, data) ? LPB_RESULT_OK : LPB_RESULT_INTERNAL_ERROR;
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

    if (!write_file_binary(context, LPB_ARTIFACT_PRIMARY_IMAGE, out_path, data)) {
        set_error(context, "Failed to write cleaned JPEG.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    return LPB_RESULT_OK;
}

static lpb_result clean_apple_image(
    lpb_context* context,
    const std::vector<uint8_t>& in_bytes,
    const std::string& out_path,
    const lpb_cleanup_action* actions,
    size_t action_count,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    std::vector<uint8_t> data = in_bytes;

    lpb_image_container img_cont = (data.size() >= 2 && data[0] == 0xFF && data[1] == 0xD8) ? LPB_IMAGE_CONTAINER_JPEG : LPB_IMAGE_CONTAINER_HEIC;
    std::vector<uint16_t> authorized_mn_tags;
    std::string fp_0011, fp_0017, fp_0025, fp_002b;
    const auto* act_0011 = find_authorized_action(actions, action_count, LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO, "apple-img-makernote-0011",
        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_EXIF_MAKERNOTE_TAG, "0x0011", "ContentIdentifier", LPB_REMOVAL_REBUILD_CONTAINER);
    const auto* act_0017 = find_authorized_action(actions, action_count, LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO, "apple-img-makernote-0017",
        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_EXIF_MAKERNOTE_TAG, "0x0017", "LivePhotoEntry17", LPB_REMOVAL_REBUILD_CONTAINER);
    const auto* act_0025 = find_authorized_action(actions, action_count, LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO, "apple-img-makernote-0025",
        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_EXIF_MAKERNOTE_TAG, "0x0025", "LivePhotoEntry25", LPB_REMOVAL_REBUILD_CONTAINER);
    const auto* act_002b = find_authorized_action(actions, action_count, LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO, "apple-img-makernote-002b",
        LPB_ARTIFACT_PRIMARY_IMAGE, LPB_RESIDUE_EXIF_MAKERNOTE_TAG, "0x002b", "LivePhotoEntry2B", LPB_REMOVAL_REBUILD_CONTAINER);

    if (act_0011) {
        if (!lpb::protocols::apple::apple_image_get_tag_fingerprint(context, data, img_cont, 0x0011, fp_0011)) {
            set_error(context, "Failed to compute residue fingerprint for apple-img-makernote-0011.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (act_0011->expected_fingerprint[0] == '\0' || fp_0011 != act_0011->expected_fingerprint) {
            set_error(context, "Residue fingerprint missing or mismatch for apple-img-makernote-0011.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        authorized_mn_tags.push_back(0x0011);
    }
    if (act_0017) {
        if (!lpb::protocols::apple::apple_image_get_tag_fingerprint(context, data, img_cont, 0x0017, fp_0017)) {
            set_error(context, "Failed to compute residue fingerprint for apple-img-makernote-0017.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (act_0017->expected_fingerprint[0] == '\0' || fp_0017 != act_0017->expected_fingerprint) {
            set_error(context, "Residue fingerprint missing or mismatch for apple-img-makernote-0017.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        authorized_mn_tags.push_back(0x0017);
    }
    if (act_0025) {
        if (!lpb::protocols::apple::apple_image_get_tag_fingerprint(context, data, img_cont, 0x0025, fp_0025)) {
            set_error(context, "Failed to compute residue fingerprint for apple-img-makernote-0025.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (act_0025->expected_fingerprint[0] == '\0' || fp_0025 != act_0025->expected_fingerprint) {
            set_error(context, "Residue fingerprint missing or mismatch for apple-img-makernote-0025.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        authorized_mn_tags.push_back(0x0025);
    }
    if (act_002b) {
        if (!lpb::protocols::apple::apple_image_get_tag_fingerprint(context, data, img_cont, 0x002b, fp_002b)) {
            set_error(context, "Failed to compute residue fingerprint for apple-img-makernote-002b.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (act_002b->expected_fingerprint[0] == '\0' || fp_002b != act_002b->expected_fingerprint) {
            set_error(context, "Residue fingerprint missing or mismatch for apple-img-makernote-002b.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        authorized_mn_tags.push_back(0x002b);
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

    if (!write_file_binary(context, LPB_ARTIFACT_PRIMARY_IMAGE, out_path, data)) {
        set_error(context, "Failed to write cleaned Apple image.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return LPB_RESULT_OK;
}

static lpb_result clean_apple_video(
    lpb_context* context,
    const std::vector<uint8_t>& in_bytes,
    const std::string& out_path,
    const lpb_cleanup_action* actions,
    size_t action_count,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    const bool should_strip_cid = find_authorized_action(actions, action_count, LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO, "apple-vid-mdta-cid",
        LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.apple.quicktime.content.identifier", "ContentIdentifier", LPB_REMOVAL_DELETE) != nullptr;
    const bool should_strip_livephoto = find_authorized_action(actions, action_count, LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO, "apple-vid-mdta-livephoto",
        LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.apple.quicktime.live-photo", "LivePhotoKey", LPB_REMOVAL_DELETE) != nullptr;

    std::vector<const char*> track_patterns;
    std::vector<std::pair<const char*, const char*>> track_residues;
    if (find_authorized_action(actions, action_count, LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO, "apple-vid-track-livephoto-info",
            LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_METADATA_TRACK, "com.apple.quicktime.live-photo-info", "LivePhotoInfoTrack", LPB_REMOVAL_DELETE)) {
        track_patterns.push_back("com.apple.quicktime.live-photo-info");
        track_residues.push_back({"apple-vid-track-livephoto-info", "Removed com.apple.quicktime.live-photo-info track"});
    }
    if (find_authorized_action(actions, action_count, LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO, "apple-vid-track-still-image-time",
            LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_METADATA_TRACK, "com.apple.quicktime.still-image-time", "StillImageTimeTrack", LPB_REMOVAL_DELETE)) {
        track_patterns.push_back("com.apple.quicktime.still-image-time");
        track_residues.push_back({"apple-vid-track-still-image-time", "Removed com.apple.quicktime.still-image-time track"});
    }
    if (find_authorized_action(actions, action_count, LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO, "apple-vid-track-transform",
            LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_METADATA_TRACK, "com.apple.quicktime.live-photo-still-image-transform", "StillImageTransformTrack", LPB_REMOVAL_DELETE)) {
        track_patterns.push_back("com.apple.quicktime.live-photo-still-image-transform");
        track_residues.push_back({"apple-vid-track-transform", "Removed com.apple.quicktime.live-photo-still-image-transform track"});
    }
    if (find_authorized_action(actions, action_count, LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO, "apple-vid-track-reference-dimensions",
            LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_METADATA_TRACK, "com.apple.quicktime.live-photo-still-image-transform-reference-dimensions", "TransformReferenceDimensionsTrack", LPB_REMOVAL_DELETE)) {
        track_patterns.push_back("com.apple.quicktime.live-photo-still-image-transform-reference-dimensions");
        track_residues.push_back({"apple-vid-track-reference-dimensions", "Removed com.apple.quicktime.live-photo-still-image-transform-reference-dimensions track"});
    }

    if (!should_strip_cid && !should_strip_livephoto && track_patterns.empty()) {
        return write_file_binary(context, LPB_ARTIFACT_MOTION_VIDEO, out_path, in_bytes) ? LPB_RESULT_OK : LPB_RESULT_INTERNAL_ERROR;
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
    spec.expected_protocol = LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO;
    spec.artifact_role = LPB_ARTIFACT_MOTION_VIDEO;
    spec.mdta_starts = starts.data();
    spec.mdta_starts_count = starts.size();
    spec.track_patterns = track_patterns.data();
    spec.track_patterns_count = track_patterns.size();
    spec.actions = actions;
    spec.action_count = action_count;

    lpb::containers::Mp4StripOutcome outcome{};
    lpb_result res = lpb::containers::stream_clean_mp4_bytes(context, std::span<const uint8_t>(in_bytes.data(), in_bytes.size()), out_path, spec, outcome);
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
    const std::vector<uint8_t>& in_bytes,
    const std::string& out_path,
    const lpb_cleanup_action* actions,
    size_t action_count,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    const auto* act_uuid = find_authorized_action(actions, action_count, LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL, "vivo-legacy-vid-uuid",
        LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_UUID_BOX, "vivoMediaExtInfo", "vivoMediaExtInfo", LPB_REMOVAL_DELETE);
    const auto* act_lp = find_authorized_action(actions, action_count, LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL, "vivo-legacy-vid-mdta-livephoto",
        LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.android.camera.livephoto", "com.android.camera.livephoto", LPB_REMOVAL_DELETE);
    const auto* act_it = find_authorized_action(actions, action_count, LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL, "vivo-legacy-vid-mdta-imagetime",
        LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.android.camera.imageTime", "com.android.camera.imageTime", LPB_REMOVAL_DELETE);
    const auto* act_gallery = find_authorized_action(actions, action_count, LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL, "vivo-legacy-vid-mdta-gallery",
        LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.vivo.gallery.livePhoto", "com.vivo.gallery.livePhoto", LPB_REMOVAL_DELETE);

    if (!act_uuid && !act_lp && !act_it && !act_gallery) {
        return write_file_binary(context, LPB_ARTIFACT_MOTION_VIDEO, out_path, in_bytes) ? LPB_RESULT_OK : LPB_RESULT_INTERNAL_ERROR;
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
    spec.expected_protocol = LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL;
    spec.artifact_role = LPB_ARTIFACT_MOTION_VIDEO;
    if (act_uuid) spec.strip_uuid_16 = vivo_uuid;
    spec.mdta_starts = starts.data();
    spec.mdta_starts_count = starts.size();
    spec.actions = actions;
    spec.action_count = action_count;

    lpb::containers::Mp4StripOutcome outcome{};
    lpb_result res = lpb::containers::stream_clean_mp4_bytes(context, std::span<const uint8_t>(in_bytes.data(), in_bytes.size()), out_path, spec, outcome);
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
    const std::vector<uint8_t>& in_bytes,
    const std::string& out_path,
    lpb_source_protocol protocol,
    const lpb_cleanup_action* actions,
    size_t action_count,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    const auto* act_openharmony = find_authorized_action(actions, action_count, protocol, "huawei-vid-mdta-openharmony",
        LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.openharmony.movingphoto", "com.openharmony.movingphoto", LPB_REMOVAL_DELETE);
    const auto* act_huawei = find_authorized_action(actions, action_count, protocol, "huawei-vid-mdta-huawei",
        LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.huawei.movingphoto", "com.huawei.movingphoto", LPB_REMOVAL_DELETE);
    const auto* act_covertime = find_authorized_action(actions, action_count, protocol, "huawei-vid-mdta-covertime",
        LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "com.openharmony.covertime", "com.openharmony.covertime", LPB_REMOVAL_DELETE);
    const auto* act_track = find_authorized_action(actions, action_count, protocol, "huawei-vid-track-movingphoto",
        LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_METADATA_TRACK, "com.openharmony.timed_metadata.movingphoto", "com.openharmony.timed_metadata.movingphoto", LPB_REMOVAL_DELETE);

    if (!act_openharmony && !act_huawei && !act_covertime && !act_track) {
        return write_file_binary(context, LPB_ARTIFACT_MOTION_VIDEO, out_path, in_bytes) ? LPB_RESULT_OK : LPB_RESULT_INTERNAL_ERROR;
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
    spec.expected_protocol = protocol;
    spec.artifact_role = LPB_ARTIFACT_MOTION_VIDEO;
    spec.mdta_starts = starts.data();
    spec.mdta_starts_count = starts.size();
    spec.mdta_contains = contains.data();
    spec.mdta_contains_count = contains.size();
    spec.track_patterns = track_patterns.data();
    spec.track_patterns_count = track_patterns.size();
    spec.actions = actions;
    spec.action_count = action_count;

    lpb::containers::Mp4StripOutcome outcome{};
    lpb_result res = lpb::containers::stream_clean_mp4_bytes(context, std::span<const uint8_t>(in_bytes.data(), in_bytes.size()), out_path, spec, outcome);
    if (res != LPB_RESULT_OK) return res;

    const char* proto_str = protocol == LPB_SOURCE_PROTOCOL_HONOR_MOVING_PHOTO ? "Honor" : "Huawei";
    if (outcome.mdta_removed) {
        for (size_t i = 0; i < starts_residues.size(); ++i) {
            if (i < outcome.mdta_starts_matched.size() && outcome.mdta_starts_matched[i]) {
                const char* fp = i < outcome.mdta_starts_fingerprints.size() ? outcome.mdta_starts_fingerprints[i].c_str() : "";
                add_fact(out_facts, proto_str, "QuickTime MDTA Keys", starts_residues[i].second,
                    starts_residues[i].first, LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "Removed", "Removed", fp);
            }
        }
        for (size_t i = 0; i < contains_residues.size(); ++i) {
            if (i < outcome.mdta_contains_matched.size() && outcome.mdta_contains_matched[i]) {
                const char* fp = i < outcome.mdta_contains_fingerprints.size() ? outcome.mdta_contains_fingerprints[i].c_str() : "";
                add_fact(out_facts, proto_str, "QuickTime MDTA Keys", contains_residues[i].second,
                    contains_residues[i].first, LPB_ARTIFACT_MOTION_VIDEO, LPB_RESIDUE_QUICKTIME_MDTA_KEY, "Removed", "Removed", fp);
            }
        }
    }
    if (outcome.track_removed && act_track) {
        if (!outcome.track_patterns_matched.empty() && outcome.track_patterns_matched[0]) {
            const char* fp = !outcome.track_fingerprints.empty() ? outcome.track_fingerprints[0].c_str() : "";
            add_fact(out_facts, proto_str, "Moving Photo metadata track", "Removed the validated movingphoto timed-metadata track",
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
    const lpb_cleanup_artifact_binding* targets,
    size_t target_count,
    const char* input_image_path,
    void* input_image_handle,
    const char* input_video_path,
    void* input_video_handle,
    const char* cleanup_source_path,
    void* cleanup_source_handle,
    const lpb_cleanup_artifact_binding* cleanup_source_target,
    const char* output_image_path,
    const char* output_video_path,
    lpb_removed_protocol_fact* out_facts,
    size_t facts_capacity,
    size_t* out_facts_count)
{
    if (!context || !facts || !input_image_path || !output_image_path) {
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (cancellation_requested_suspended(context)) {
        return LPB_RESULT_CANCELLED;
    }
    if (!actions || action_count == 0) {
        set_error(context, "No cleanup actions authorized in plan.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    if (!targets || target_count == 0) {
        set_error(context, "No artifact snapshot targets provided for TOCTOU verification.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    const lpb_cleanup_artifact_binding* primary_target = nullptr;
    const lpb_cleanup_artifact_binding* video_target = nullptr;
    for (size_t i = 0; i < target_count; ++i) {
        if (targets[i].artifact_role == LPB_ARTIFACT_PRIMARY_IMAGE) {
            if (primary_target != nullptr) {
                set_error(context, "Duplicate PrimaryImage artifact target provided. At most one PrimaryImage target is allowed.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            primary_target = &targets[i];
        } else if (targets[i].artifact_role == LPB_ARTIFACT_MOTION_VIDEO) {
            if (video_target != nullptr) {
                set_error(context, "Duplicate MotionVideo artifact target provided. At most one MotionVideo target is allowed.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            video_target = &targets[i];
        } else {
            set_error(context, "Unsupported or unknown artifact target role provided in cleanup plan.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
    }

    if (!primary_target || primary_target->has_expected_sha256 != 1 || primary_target->expected_length == 0 || is_all_zeroes_32(primary_target->expected_sha256)) {
        set_error(context, "TOCTOU check failed: valid primary image expected SHA-256 and positive length are mandatory.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    if ((cleanup_source_path == nullptr) != (cleanup_source_target == nullptr)) {
        set_error(context, "Cleanup source path and SourceContainer target must be supplied together.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (cleanup_source_path && cleanup_source_target) {
        if (facts->protocol != LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG ||
            cleanup_source_path[0] == '\0' ||
            cleanup_source_target->artifact_role != LPB_ARTIFACT_SOURCE_CONTAINER ||
            cleanup_source_target->has_expected_sha256 != 1 ||
            cleanup_source_target->expected_length == 0 ||
            is_all_zeroes_32(cleanup_source_target->expected_sha256)) {
            set_error(context, "Cleanup source is supported only as a complete Samsung JPEG SourceContainer snapshot.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (is_all_zeroes_32(facts->primary_sha256) ||
            std::memcmp(cleanup_source_target->expected_sha256, facts->primary_sha256, 32) != 0) {
            set_error(context, "Cleanup source SHA-256 does not match the Inspector-confirmed primary source identity.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
    }

    constexpr uint64_t kMaxSnapshotBytes = 2ULL * 1024 * 1024 * 1024; // 2GB limit for in-memory snapshot
    if (primary_target->expected_length > kMaxSnapshotBytes) {
        set_error(context, "Primary image artifact exceeds maximum supported in-memory snapshot size (2GB).");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (video_target && video_target->expected_length > kMaxSnapshotBytes) {
        set_error(context, "Motion video artifact exceeds maximum supported in-memory snapshot size (2GB). Low-memory streaming is deferred.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (cleanup_source_target && cleanup_source_target->expected_length > kMaxSnapshotBytes) {
        set_error(context, "Cleanup source exceeds maximum supported in-memory snapshot size (2GB).");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::vector<uint8_t> input_image_bytes;
    const bool image_read_ok = input_image_handle != nullptr
        ? read_file_binary_handle(input_image_handle, input_image_bytes)
        : read_file_binary(input_image_path, input_image_bytes);
    if (!image_read_ok) {
        set_error(context, "Failed to read primary image artifact snapshot.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    if (input_image_bytes.size() != primary_target->expected_length) {
        set_error(context, "TOCTOU check failed: input image artifact length mismatch.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    uint8_t actual_image_sha[32]{};
    lpb::crypto::sha256_buffer(input_image_bytes.data(), input_image_bytes.size(), actual_image_sha);
    if (std::memcmp(actual_image_sha, primary_target->expected_sha256, 32) != 0) {
        set_error(context, "TOCTOU check failed: input image artifact SHA-256 mismatch.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::vector<uint8_t> cleanup_source_bytes;
    if (cleanup_source_path && cleanup_source_target) {
        const bool source_read_ok = cleanup_source_handle != nullptr
            ? read_file_binary_handle(cleanup_source_handle, cleanup_source_bytes)
            : read_file_binary(cleanup_source_path, cleanup_source_bytes);
        if (!source_read_ok) {
            set_error(context, "Failed to read cleanup source container snapshot.");
            return LPB_RESULT_INTERNAL_ERROR;
        }
        if (cleanup_source_bytes.size() != cleanup_source_target->expected_length) {
            set_error(context, "TOCTOU check failed: cleanup source container length mismatch.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        uint8_t actual_cleanup_source_sha[32]{};
        lpb::crypto::sha256_buffer(
            cleanup_source_bytes.data(), cleanup_source_bytes.size(), actual_cleanup_source_sha);
        if (std::memcmp(actual_cleanup_source_sha, cleanup_source_target->expected_sha256, 32) != 0) {
            set_error(context, "TOCTOU check failed: cleanup source container SHA-256 mismatch.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
    }

    std::vector<uint8_t> input_video_bytes;
    if (input_video_path) {
        if (!video_target || video_target->has_expected_sha256 != 1 || video_target->expected_length == 0 || is_all_zeroes_32(video_target->expected_sha256)) {
            set_error(context, "TOCTOU check failed: valid motion video expected SHA-256 and positive length are mandatory when video input is provided.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        const bool video_read_ok = input_video_handle != nullptr
            ? read_file_binary_handle(input_video_handle, input_video_bytes)
            : read_file_binary(input_video_path, input_video_bytes);
        if (!video_read_ok) {
            set_error(context, "Failed to read motion video artifact snapshot.");
            return LPB_RESULT_INTERNAL_ERROR;
        }
        if (input_video_bytes.size() != video_target->expected_length) {
            set_error(context, "TOCTOU check failed: input video artifact length mismatch.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        uint8_t actual_video_sha[32]{};
        lpb::crypto::sha256_buffer(input_video_bytes.data(), input_video_bytes.size(), actual_video_sha);
        if (std::memcmp(actual_video_sha, video_target->expected_sha256, 32) != 0) {
            set_error(context, "TOCTOU check failed: input video artifact SHA-256 mismatch.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
    }

    if (context && context->cleaner_post_snapshot_callback) {
        // External callback boundary: suspend the raw-writer capability so a
        // re-entrant call from the hook can never borrow the cleaner's window.
        cleaner_authority_suspend suspend(context);
        context->cleaner_post_snapshot_callback(context);
    }

    if (cancellation_requested_suspended(context)) {
        return LPB_RESULT_CANCELLED;
    }

    std::unordered_set<std::string_view> seen_residues;
    for (size_t i = 0; i < action_count; ++i) {
        const auto& a = actions[i];
        if (a.owner_protocol != facts->protocol) {
            set_error(context, "Action owner protocol mismatch with detected source protocol.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (a.residue_id[0] == '\0') {
            set_error(context, "Action residue_id must not be empty.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (a.coordinate_space < LPB_COORD_ORIGINAL_SOURCE_RANGE || a.coordinate_space > LPB_COORD_STRUCTURED_SELECTOR) {
            set_error(context, "Action coordinate_space is invalid or unsupported.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (!seen_residues.insert(a.residue_id).second) {
            set_error(context, "Duplicate residue_id in cleanup actions.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (a.is_mandatory && a.expected_fingerprint[0] == '\0') {
            set_error(context, "Mandatory action must specify non-empty expected_fingerprint.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
    }

    if (paths_alias(input_image_path, output_image_path) ||
        (input_video_path && output_image_path && paths_alias(input_video_path, output_image_path)) ||
        (cleanup_source_path && output_image_path && paths_alias(cleanup_source_path, output_image_path)) ||
        (input_image_path && output_video_path && paths_alias(input_image_path, output_video_path)) ||
        (input_video_path && output_video_path && paths_alias(input_video_path, output_video_path)) ||
        (cleanup_source_path && output_video_path && paths_alias(cleanup_source_path, output_video_path)) ||
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
            res = clean_apple_image(context, input_image_bytes, output_image_path, actions, action_count, removed_facts);
            if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
                res = clean_apple_video(context, input_video_bytes, output_video_path, actions, action_count, removed_facts);
            }
            break;

        case LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1:
        case LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2:
        case LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO:
        case LPB_SOURCE_PROTOCOL_VIVO_X300:
            res = clean_jpeg_xmp(context, input_image_bytes, output_image_path,
                static_cast<lpb_source_protocol>(facts->protocol), actions, action_count, true, removed_facts);
            if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
                res = write_file_binary(context, LPB_ARTIFACT_MOTION_VIDEO, output_video_path, input_video_bytes) ? LPB_RESULT_OK : LPB_RESULT_INTERNAL_ERROR;
            }
            break;

        case LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL:
            res = write_file_binary(context, LPB_ARTIFACT_PRIMARY_IMAGE, output_image_path, input_image_bytes) ? LPB_RESULT_OK : LPB_RESULT_INTERNAL_ERROR;
            if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
                res = clean_vivo_legacy_video(context, input_video_bytes, output_video_path, actions, action_count, removed_facts);
            }
            break;

        case LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG:
            if (facts->preservation_carrier_count > 0 && !cleanup_source_path) {
                set_error(context, "Samsung SEF preservation requires a complete cleanup source container.");
                res = LPB_RESULT_INVALID_ARGUMENT;
                break;
            }
            res = protocols::clean::clean_samsung_sef_jpeg(
                context,
                cleanup_source_bytes.empty() ? input_image_bytes : cleanup_source_bytes,
                output_image_path,
                actions,
                action_count,
                removed_facts);
            if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
                res = write_file_binary(context, LPB_ARTIFACT_MOTION_VIDEO, output_video_path, input_video_bytes) ? LPB_RESULT_OK : LPB_RESULT_INTERNAL_ERROR;
            }
            break;

        case LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC:
            res = protocols::clean::clean_samsung_heic(context, input_image_bytes, output_image_path, actions, action_count, removed_facts);
            if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
                res = write_file_binary(context, LPB_ARTIFACT_MOTION_VIDEO, output_video_path, input_video_bytes) ? LPB_RESULT_OK : LPB_RESULT_INTERNAL_ERROR;
            }
            break;

        case LPB_SOURCE_PROTOCOL_HUAWEI_MOVING_PHOTO:
        case LPB_SOURCE_PROTOCOL_HONOR_MOVING_PHOTO:
            res = write_file_binary(context, LPB_ARTIFACT_PRIMARY_IMAGE, output_image_path, input_image_bytes) ? LPB_RESULT_OK : LPB_RESULT_INTERNAL_ERROR;
            if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
                res = clean_huawei_video(context, input_video_bytes, output_video_path,
                    static_cast<lpb_source_protocol>(facts->protocol), actions, action_count, removed_facts);
            }
            break;

        case LPB_SOURCE_PROTOCOL_NON_LIVE:
        default:
            res = write_file_binary(context, LPB_ARTIFACT_PRIMARY_IMAGE, output_image_path, input_image_bytes) ? LPB_RESULT_OK : LPB_RESULT_INTERNAL_ERROR;
            if (res == LPB_RESULT_OK && input_video_path && output_video_path) {
                res = write_file_binary(context, LPB_ARTIFACT_MOTION_VIDEO, output_video_path, input_video_bytes) ? LPB_RESULT_OK : LPB_RESULT_INTERNAL_ERROR;
            }
            break;
        }

        if (res == LPB_RESULT_OK && removed_facts.size() > facts_capacity) {
            if (out_facts_count) *out_facts_count = removed_facts.size();
            // The published objects and the ownership registry are left intact.
            // The Native cleaner never pathname-deletes here: only the
            // transaction rollback (exact verified handle + FileDispositionInfo)
            // may remove the objects this transaction created, so a
            // same-content replacement or a foreign object that took over a
            // path is never at risk.  The caller can either retry with a
            // larger buffer or roll back through the registry.
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

static void* verify_and_open_input(
    lpb_context* context,
    const lpb_published_artifact_record* expected,
    const char* input_path,
    const char* role_name) noexcept
{
    // The function is noexcept (ABI contract) but must fail closed instead of
    // terminating when a filesystem/library allocation throws: the whole body
    // runs inside a catch-all that converts any exception into an identity
    // failure.  Any handle opened before the throw is closed here.
    HANDLE opened = INVALID_HANDLE_VALUE;
    try
    {
        if (expected == nullptr || input_path == nullptr || input_path[0] == '\0')
        {
            set_error(context, "[ObjectIdentity] The cleanup plan does not authorize an input object for this role.");
            return nullptr;
        }

        const auto path = utf8_to_path(input_path);
        const DWORD attributes = GetFileAttributesW(path.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES)
        {
            set_error(context, "[ObjectIdentity] A cleanup input object could not be inspected before mutation.");
            return nullptr;
        }
        if ((attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            set_error(context, "[ObjectIdentity] A cleanup input resolves through a reparse point; the plan never authorizes reparse targets.");
            return nullptr;
        }

        opened = CreateFileW(
            path.c_str(),
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_BACKUP_SEMANTICS,
            nullptr);
        if (opened == INVALID_HANDLE_VALUE)
        {
            set_error(context, "[ObjectIdentity] A cleanup input object could not be opened for exact-object verification.");
            return nullptr;
        }

        lpb_file_identity actual{};
        std::wstring actual_path;
        if (!capture_file_identity_from_handle(opened, actual, actual_path))
        {
            CloseHandle(opened);
            opened = INVALID_HANDLE_VALUE;
            set_error(context, "[ObjectIdentity] A cleanup input object could not be verified from its open handle.");
            return nullptr;
        }

        bool path_matches = false;
        try
        {
            path_matches = _wcsicmp(actual_path.c_str(), expected->final_path.c_str()) == 0;
        }
        catch (...)
        {
            path_matches = false;
        }

        const bool same_object =
            actual.volume_serial == expected->identity.volume_serial &&
            actual.file_index == expected->identity.file_index;
        const bool single_link = actual.link_count == 1;
        const bool size_matches = actual.file_size == expected->byte_length;

        if (!same_object || !single_link || !size_matches || !path_matches)
        {
            CloseHandle(opened);
            opened = INVALID_HANDLE_VALUE;
            std::string message = "[ObjectIdentity] The cleanup input (role: ";
            message += role_name == nullptr ? "unknown" : role_name;
            message += ") is not the exact filesystem object the plan authorized (replaced, relinked, or re-pointed object detected).";
            set_error(context, message.c_str());
            return nullptr;
        }

        // The caller reads and mutates through this same handle; no pathname
        // reopen window exists between verification and the snapshot.
        return opened;
    }
    catch (...)
    {
        if (opened != nullptr && opened != INVALID_HANDLE_VALUE)
        {
            CloseHandle(opened);
        }
        set_error(context, "[ObjectIdentity] Failed to verify the cleanup input object (unhandled native exception); fail closed.");
        return nullptr;
    }
}

struct input_handle_guard
{
    void* handle;
    explicit input_handle_guard(void* h) noexcept : handle(h) {}
    ~input_handle_guard() noexcept
    {
        if (handle != nullptr && handle != INVALID_HANDLE_VALUE)
        {
            CloseHandle(static_cast<HANDLE>(handle));
        }
    }
};

lpb_result clean_source_protocol_with_cleanup_plan(
    lpb_context* context,
    const lpb_cleanup_plan_record& plan,
    const char* input_image_path,
    const char* input_video_path,
    const char* cleanup_source_path,
    const char* output_image_path,
    const char* output_video_path,
    lpb_removed_protocol_fact* out_facts,
    size_t facts_capacity,
    size_t* out_facts_count)
{
    if (out_facts_count) *out_facts_count = 0;
    if (context == nullptr || input_image_path == nullptr || output_image_path == nullptr)
    {
        set_error(context, "[AuthorityViolation] Context, input image, and output image paths are required.");
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }
    if (cancellation_requested_suspended(context))
    {
        return LPB_RESULT_CANCELLED;
    }
    if (plan.confirmed_residues.empty())
    {
        set_error(context, "[AuthorityViolation] The cleanup plan authorizes no protocol residues.");
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }

    // The whole plan-authorized mutation body is exception-safe: any
    // allocation/format failure becomes a fail-closed internal error instead
    // of terminating the process (0xC0000409).  RAII guards still release
    // handles and the thread-local authority binding during unwinding.
    try
    {
    // Resolve the plan-owned artifacts by role.
    const lpb_published_artifact_record* primary_record = nullptr;
    const lpb_published_artifact_record* video_record = nullptr;
    const lpb_published_artifact_record* source_record = nullptr;
    for (const auto& artifact : plan.artifacts)
    {
        if (artifact.artifact_role == LPB_ARTIFACT_PRIMARY_IMAGE)
        {
            if (primary_record != nullptr)
            {
                set_error(context, "[AuthorityViolation] The cleanup plan carries duplicate PrimaryImage ownership records.");
                return LPB_RESULT_AUTHORITY_VIOLATION;
            }
            primary_record = &artifact;
        }
        else if (artifact.artifact_role == LPB_ARTIFACT_MOTION_VIDEO)
        {
            if (video_record != nullptr)
            {
                set_error(context, "[AuthorityViolation] The cleanup plan carries duplicate MotionVideo ownership records.");
                return LPB_RESULT_AUTHORITY_VIOLATION;
            }
            video_record = &artifact;
        }
        else if (artifact.artifact_role == LPB_ARTIFACT_SOURCE_CONTAINER)
        {
            if (source_record != nullptr)
            {
                set_error(context, "[AuthorityViolation] The cleanup plan carries duplicate SourceContainer ownership records.");
                return LPB_RESULT_AUTHORITY_VIOLATION;
            }
            source_record = &artifact;
        }
    }

    // Exact-object identity gate BEFORE any byte is read or written.  Each
    // input is opened once, verified from its open handle, and the same handle
    // is used for the snapshot read: there is no pathname-reopen window in
    // which a same-content replacement could slip through.
    void* image_handle = nullptr;
    void* video_handle = nullptr;
    void* source_handle = nullptr;
    // Function-scoped RAII ownership: each verified handle is assigned to its
    // guard immediately after verification, so every early-return path closes
    // exactly the handles that were opened, and the guards stay alive until
    // the shared core has finished reading through them.
    input_handle_guard image_guard(nullptr);
    input_handle_guard video_guard(nullptr);
    input_handle_guard source_guard(nullptr);
    image_handle = lpb::media::verify_and_open_input(context, primary_record, input_image_path, "primary image");
    if (image_handle == nullptr) return LPB_RESULT_AUTHORITY_VIOLATION;
    image_guard.handle = image_handle;
    if (input_video_path != nullptr && input_video_path[0] != '\0')
    {
        video_handle = lpb::media::verify_and_open_input(context, video_record, input_video_path, "motion video");
        if (video_handle == nullptr) return LPB_RESULT_AUTHORITY_VIOLATION;
        video_guard.handle = video_handle;
    }
    if (cleanup_source_path != nullptr && cleanup_source_path[0] != '\0')
    {
        source_handle = lpb::media::verify_and_open_input(context, source_record, cleanup_source_path, "cleanup source");
        if (source_handle == nullptr) return LPB_RESULT_AUTHORITY_VIOLATION;
        source_guard.handle = source_handle;
    }
    if ((input_video_path == nullptr || input_video_path[0] == '\0') && video_record != nullptr)
    {
        set_error(context, "[AuthorityViolation] The cleanup plan owns a MotionVideo object but no motion video input was supplied.");
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }
    if ((cleanup_source_path == nullptr || cleanup_source_path[0] == '\0') && source_record != nullptr)
    {
        set_error(context, "[AuthorityViolation] The cleanup plan owns a SourceContainer object but no cleanup source input was supplied.");
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }

    // Build the DTO views from the Native plan record.  These are derived
    // from the authoritative record, never accepted from a caller.
    std::vector<lpb_cleanup_action> actions;
    actions.reserve(plan.confirmed_residues.size());
    for (const auto& residue : plan.confirmed_residues)
    {
        lpb_cleanup_action action{};
        action.struct_size = sizeof(action);
        strncpy_s(action.residue_id, residue.residue_id, _TRUNCATE);
        action.owner_protocol = residue.owner_protocol;
        action.artifact_role = residue.artifact_role;
        action.structure_kind = residue.structure_kind;
        strncpy_s(action.selector, residue.selector, _TRUNCATE);
        strncpy_s(action.expected_semantic, residue.expected_semantic, _TRUNCATE);
        strncpy_s(action.expected_fingerprint, residue.expected_fingerprint, _TRUNCATE);
        action.coordinate_space = residue.coordinate_space;
        action.removal_mode = residue.removal_mode;
        action.is_mandatory = residue.required_after_extraction;
        actions.push_back(action);
    }

    // The shared clean core consumes a Primary/Motion target list plus a
    // separate SourceContainer target.  The SourceContainer ownership record
    // must never leak into the generic target list (which rejects it), so it
    // is projected onto its own dedicated binding.
    std::vector<lpb_cleanup_artifact_binding> targets;
    targets.reserve(plan.artifacts.size());
    lpb_cleanup_artifact_binding source_binding{};
    source_binding.struct_size = sizeof(source_binding);
    source_binding.artifact_role = LPB_ARTIFACT_SOURCE_CONTAINER;
    bool source_present = false;
    for (const auto& artifact : plan.artifacts)
    {
        if (artifact.artifact_role == LPB_ARTIFACT_SOURCE_CONTAINER)
        {
            source_binding.expected_length = artifact.byte_length;
            std::copy(artifact.sha256.begin(), artifact.sha256.end(), source_binding.expected_sha256);
            source_binding.has_expected_sha256 = 1;
            source_present = true;
            continue;
        }
        if (artifact.artifact_role != LPB_ARTIFACT_PRIMARY_IMAGE &&
            artifact.artifact_role != LPB_ARTIFACT_MOTION_VIDEO)
        {
            continue;
        }
        lpb_cleanup_artifact_binding target{};
        target.struct_size = sizeof(target);
        target.artifact_role = artifact.artifact_role;
        target.expected_length = artifact.byte_length;
        std::copy(artifact.sha256.begin(), artifact.sha256.end(), target.expected_sha256);
        target.has_expected_sha256 = 1;
        targets.push_back(target);
    }

    const lpb_cleanup_artifact_binding* source_target = source_present ? &source_binding : nullptr;

    // Capability gate for low-level in-place destructive primitives.
    cleaner_authority_guard authority(context);

    const lpb_result clean_result = clean_source_protocol_with_plan(
        context, &plan.facts, actions.data(), actions.size(),
        targets.data(), targets.size(),
        input_image_path, image_handle,
        input_video_path, video_handle,
        cleanup_source_path, source_handle,
        source_target,
        output_image_path, output_video_path,
        out_facts, facts_capacity, out_facts_count);
    return clean_result;
    }
    catch (const std::exception& ex)
    {
        set_error(context, ex.what());
        return LPB_RESULT_INTERNAL_ERROR;
    }
    catch (...)
    {
        set_error(context, "[InternalError] Unhandled native exception during plan-authorized cleaning; fail closed.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
}

} // namespace lpb::media

