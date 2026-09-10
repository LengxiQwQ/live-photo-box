#include "media/media_extractor.h"
#include "foundation/internal.h"
#include "foundation/sha256.h"
#include <filesystem>
#include <Windows.h>
#include <vector>
#include <string>
#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstring>

namespace lpb::media {

namespace {

struct slice_task {
    HANDLE src_handle{INVALID_HANDLE_VALUE};
    const char* src_path{nullptr};
    uint64_t offset{0};
    uint64_t length{0};
    const char* dst_path{nullptr};
    std::filesystem::path final_dst_path;
    std::filesystem::path temp_path;
    HANDLE temp_handle{INVALID_HANDLE_VALUE};
    HANDLE destination_directory_handle{INVALID_HANDLE_VALUE};
    lpb_file_identity temp_identity{};
    uint8_t expected_slice_sha256[32]{};
    uint8_t staged_sha256[32]{};
    bool published{false};
    int32_t target_artifact{0}; // 0 = PrimaryImage, 1 = MotionVideo, 2 = GainMap, 3+ = auxiliary index
    const char* artifact_name{"Unknown"};
};

struct extraction_failure {
    lpb_result result{LPB_RESULT_INTERNAL_ERROR};
    std::string category;
    std::string details;
};

static lpb_result rollback_extraction_transaction(
    lpb_context* context,
    const extraction_failure& original_failure,
    std::vector<slice_task>& tasks) noexcept
{
    std::string cleanup_fail_path;
    DWORD cleanup_fail_err = 0;
    bool all_clean = true;

    const bool fail_cleanup = context &&
        (((static_cast<uint32_t>(context->extractor_hook.fault) & 0x80) != 0) ||
         context->extractor_hook.fault == LPB_EXTRACTOR_FAULT_CLEANUP_FAIL);

    // Roll back only through handles owned by this transaction.  In
    // particular, never delete by the old path: after a race that path may
    // now name a third-party replacement.
    for (auto& task : tasks) {
        if (task.temp_handle == INVALID_HANDLE_VALUE || task.temp_handle == NULL) {
            if (task.destination_directory_handle != INVALID_HANDLE_VALUE &&
                task.destination_directory_handle != NULL) {
                CloseHandle(task.destination_directory_handle);
                task.destination_directory_handle = INVALID_HANDLE_VALUE;
            }
            continue;
        }

        if (fail_cleanup) {
            all_clean = false;
            if (cleanup_fail_path.empty()) {
                cleanup_fail_path = path_to_utf8(task.final_dst_path.empty() ? task.temp_path : task.final_dst_path);
                cleanup_fail_err = ERROR_ACCESS_DENIED;
            }
            CloseHandle(task.temp_handle);
            task.temp_handle = INVALID_HANDLE_VALUE;
            if (task.destination_directory_handle != INVALID_HANDLE_VALUE &&
                task.destination_directory_handle != NULL) {
                CloseHandle(task.destination_directory_handle);
                task.destination_directory_handle = INVALID_HANDLE_VALUE;
            }
            continue;
        }

        FILE_DISPOSITION_INFO disposition{};
        disposition.DeleteFile = TRUE;
        if (!SetFileInformationByHandle(
                task.temp_handle,
                FileDispositionInfo,
                &disposition,
                sizeof(disposition))) {
            const DWORD err = GetLastError();
            all_clean = false;
            if (cleanup_fail_path.empty()) {
                cleanup_fail_path = path_to_utf8(task.final_dst_path.empty() ? task.temp_path : task.final_dst_path);
                cleanup_fail_err = err;
            }
        }

        if (!CloseHandle(task.temp_handle)) {
            const DWORD err = GetLastError();
            all_clean = false;
            if (cleanup_fail_path.empty()) {
                cleanup_fail_path = path_to_utf8(task.final_dst_path.empty() ? task.temp_path : task.final_dst_path);
                cleanup_fail_err = err;
            }
        }
        task.temp_handle = INVALID_HANDLE_VALUE;
        if (task.destination_directory_handle != INVALID_HANDLE_VALUE &&
            task.destination_directory_handle != NULL) {
            if (!CloseHandle(task.destination_directory_handle)) {
                const DWORD err = GetLastError();
                all_clean = false;
                if (cleanup_fail_path.empty()) {
                    cleanup_fail_path = path_to_utf8(task.final_dst_path.empty() ? task.temp_path : task.final_dst_path);
                    cleanup_fail_err = err;
                }
            }
            task.destination_directory_handle = INVALID_HANDLE_VALUE;
        }
    }

    if (!all_clean) {
        std::string msg = "[CleanupFailed] Original failure: " + original_failure.category + " " + original_failure.details +
            "; Rollback cleanup failed on '" + cleanup_fail_path + "' (Win32 error: " + std::to_string(cleanup_fail_err) + ").";
        set_error(context, msg.c_str());
        return LPB_RESULT_INTERNAL_ERROR;
    }

    std::string msg = original_failure.category + " " + original_failure.details;
    set_error(context, msg.c_str());
    return original_failure.result;
}

struct handle_guard {
    HANDLE h{INVALID_HANDLE_VALUE};
    ~handle_guard() {
        if (h != INVALID_HANDLE_VALUE && h != NULL) {
            CloseHandle(h);
        }
    }
};

bool same_file_identity(const lpb_file_identity& left, const lpb_file_identity& right) noexcept
{
    return left.volume_serial == right.volume_serial &&
        left.file_index == right.file_index &&
        left.file_size == right.file_size;
}

bool same_object_identity(const lpb_file_identity& left, const lpb_file_identity& right) noexcept
{
    return left.volume_serial == right.volume_serial && left.file_index == right.file_index;
}

std::wstring normalized_absolute_path(const std::filesystem::path& path) noexcept
{
    try {
        std::error_code ec;
        auto absolute = std::filesystem::absolute(path, ec);
        if (ec) absolute = path;
        std::wstring result = absolute.lexically_normal().wstring();
        if (result.size() >= 4 && result.compare(0, 4, L"\\\\?\\") == 0) {
            result.erase(0, 4);
        }
        return result;
    } catch (...) {
        return {};
    }
}

bool path_matches_handle(
    HANDLE handle,
    const std::filesystem::path& expected_path,
    const lpb_file_identity& expected_identity) noexcept
{
    lpb_file_identity actual_identity{};
    std::wstring actual_path;
    if (!capture_file_identity_from_handle(handle, actual_identity, actual_path) ||
        !same_object_identity(actual_identity, expected_identity)) {
        return false;
    }

    std::wstring normalized_actual = actual_path;
    if (normalized_actual.size() >= 4 && normalized_actual.compare(0, 4, L"\\\\?\\") == 0) {
        normalized_actual.erase(0, 4);
    }
    const std::wstring normalized_expected = normalized_absolute_path(expected_path);
    return !normalized_expected.empty() &&
        _wcsicmp(normalized_actual.c_str(), normalized_expected.c_str()) == 0;
}

bool path_is_reparse_point(const std::filesystem::path& path) noexcept
{
    if (path.empty()) return false;
    HANDLE handle = CreateFileW(
        path.c_str(),
        FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS,
        nullptr);
    if (handle == INVALID_HANDLE_VALUE) return false;

    FILE_ATTRIBUTE_TAG_INFO tag_info{};
    const bool result = GetFileInformationByHandleEx(
        handle,
        FileAttributeTagInfo,
        &tag_info,
        sizeof(tag_info)) != FALSE &&
        (((tag_info.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) ||
         tag_info.ReparseTag != 0);
    CloseHandle(handle);
    return result;
}

bool open_destination_directory(
    const std::filesystem::path& directory,
    HANDLE& handle) noexcept
{
    handle = INVALID_HANDLE_VALUE;
    if (directory.empty() || path_is_reparse_point(directory)) {
        return false;
    }

    handle = CreateFileW(
        directory.c_str(),
        FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES,
        // Do not share DELETE. This keeps the directory object used by the
        // relative rename stable until publish and post-publish validation.
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr,
        OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
        nullptr);
    if (handle == INVALID_HANDLE_VALUE) {
        return false;
    }

    lpb_file_identity identity{};
    std::wstring actual_path;
    if (!capture_file_identity_from_handle(handle, identity, actual_path)) {
        CloseHandle(handle);
        handle = INVALID_HANDLE_VALUE;
        return false;
    }

    std::wstring normalized_actual = actual_path;
    if (normalized_actual.size() >= 4 && normalized_actual.compare(0, 4, L"\\\\?\\") == 0) {
        normalized_actual.erase(0, 4);
    }
    const std::wstring normalized_expected = normalized_absolute_path(directory);
    if (normalized_expected.empty() || _wcsicmp(normalized_actual.c_str(), normalized_expected.c_str()) != 0) {
        CloseHandle(handle);
        handle = INVALID_HANDLE_VALUE;
        return false;
    }
    return true;
}

std::atomic<uint64_t> g_temp_sequence{1};

bool open_unique_temp_file(
    const std::filesystem::path& directory,
    std::filesystem::path& path,
    HANDLE& handle) noexcept
{
    handle = INVALID_HANDLE_VALUE;
    const DWORD process_id = GetCurrentProcessId();
    for (uint32_t attempt = 0; attempt != 64; ++attempt) {
        const uint64_t sequence = g_temp_sequence.fetch_add(1, std::memory_order_relaxed);
        const std::wstring name = L".lpb-" + std::to_wstring(process_id) + L"-" +
            std::to_wstring(sequence) + L".tmp";
        const auto candidate = directory / name;
        HANDLE candidate_handle = CreateFileW(
            candidate.c_str(),
            GENERIC_READ | GENERIC_WRITE | DELETE,
            FILE_SHARE_READ | FILE_SHARE_DELETE,
            nullptr,
            CREATE_NEW,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (candidate_handle != INVALID_HANDLE_VALUE) {
            path = candidate;
            handle = candidate_handle;
            return true;
        }
        if (GetLastError() != ERROR_FILE_EXISTS && GetLastError() != ERROR_ALREADY_EXISTS) {
            return false;
        }
    }
    SetLastError(ERROR_FILE_EXISTS);
    return false;
}

bool same_bounded_string_n(const char* left, const char* right, size_t capacity) noexcept
{
    const void* left_end = std::memchr(left, '\0', capacity);
    const void* right_end = std::memchr(right, '\0', capacity);
    if (!left_end || !right_end) return false;
    const size_t left_length = static_cast<const char*>(left_end) - left;
    const size_t right_length = static_cast<const char*>(right_end) - right;
    return left_length == right_length && std::memcmp(left, right, left_length) == 0;
}

bool same_bounded_string(const char* left, const char* right) noexcept
{
    return same_bounded_string_n(left, right, 64);
}

bool hash_is_nonzero(const uint8_t hash[32]) noexcept
{
    for (size_t i = 0; i < 32; ++i) if (hash[i] != 0) return true;
    return false;
}

bool bounded_text_present(const char* value, size_t capacity) noexcept
{
    if (!value) return false;
    const char* end = static_cast<const char*>(std::memchr(value, '\0', capacity));
    return end != nullptr && end != value;
}

bool bounded_equals_literal(const char* value, size_t capacity, const char* literal) noexcept
{
    if (!value || !literal) return false;
    const void* end = std::memchr(value, '\0', capacity);
    if (!end) return false;
    const size_t length = static_cast<const char*>(end) - value;
    const size_t literal_length = std::strlen(literal);
    return length == literal_length && std::memcmp(value, literal, literal_length) == 0;
}

const lpb_extraction_output* find_auxiliary_output(
    const lpb_extraction_output* outputs,
    size_t output_count,
    uint32_t auxiliary_index) noexcept
{
    if (!outputs) return nullptr;
    for (size_t i = 0; i < output_count; ++i) {
        if (outputs[i].auxiliary_index == auxiliary_index) return &outputs[i];
    }
    return nullptr;
}

bool validate_plan_relationships(
    lpb_context* context,
    const lpb_source_media_facts* facts) noexcept
{
    if (!facts || facts->struct_size < sizeof(lpb_source_media_facts) || facts->auxiliary_count > 8)
    {
        set_error(context, "[AuthorityViolation] Extraction plan auxiliary facts are incompatible.");
        return false;
    }

    for (uint32_t i = 0; i < facts->auxiliary_count; ++i)
    {
        const auto& auxiliary = facts->auxiliary_items[i];
        if (auxiliary.struct_size < sizeof(lpb_auxiliary_item_facts) ||
            auxiliary.is_present != 1 ||
            auxiliary.container == LPB_IMAGE_CONTAINER_UNKNOWN ||
            auxiliary.container > LPB_IMAGE_CONTAINER_HEIC ||
            auxiliary.representation < LPB_AUX_REPRESENTATION_EMBEDDED ||
            auxiliary.representation > LPB_AUX_REPRESENTATION_MATERIALIZED ||
            auxiliary.ownership < LPB_AUX_OWNER_PRIMARY ||
            auxiliary.ownership > LPB_AUX_OWNER_AUXILIARY ||
            auxiliary.codec < LPB_AUX_CODEC_JPEG ||
            auxiliary.codec > LPB_AUX_CODEC_COPY ||
            auxiliary.item_id == 0 ||
            !bounded_text_present(auxiliary.relationship, sizeof(auxiliary.relationship)) ||
            !bounded_text_present(auxiliary.semantic, sizeof(auxiliary.semantic)) ||
            !bounded_text_present(auxiliary.stable_identity, sizeof(auxiliary.stable_identity)) ||
            !bounded_text_present(auxiliary.owner_identity, sizeof(auxiliary.owner_identity)) ||
            !hash_is_nonzero(auxiliary.sha256) ||
            auxiliary.file_range.length == 0 ||
            auxiliary.source_index < 0 || auxiliary.source_index > 1 ||
            (auxiliary.ownership == LPB_AUX_OWNER_PRIMARY &&
                !bounded_equals_literal(auxiliary.owner_identity, sizeof(auxiliary.owner_identity), "primary:0")) ||
            (auxiliary.ownership == LPB_AUX_OWNER_AUXILIARY &&
                bounded_equals_literal(auxiliary.owner_identity, sizeof(auxiliary.owner_identity), "primary:0")))
        {
            set_error(context, "[AuthorityViolation] Extraction plan contains an incomplete auxiliary relationship.");
            return false;
        }

        if (!auxiliary.is_present) continue;
        for (uint32_t j = 0; j < i; ++j)
        {
            const auto& previous = facts->auxiliary_items[j];
            if (previous.is_present &&
                (previous.item_id == auxiliary.item_id ||
                 same_bounded_string_n(previous.stable_identity, auxiliary.stable_identity, 96) ||
                 (previous.source_index == auxiliary.source_index &&
                  previous.file_range.offset == auxiliary.file_range.offset &&
                  previous.file_range.length == auxiliary.file_range.length &&
                  same_bounded_string_n(previous.semantic, auxiliary.semantic, 64))))
            {
                set_error(context, "[AuthorityViolation] Extraction plan contains duplicate auxiliary identity or source relationship.");
                return false;
            }
        }
    }

    if (!facts->gain_map.is_present) return true;
    if (facts->gain_map.struct_size < sizeof(lpb_gainmap_item_facts) ||
        facts->gain_map.file_range.length == 0 ||
        facts->gain_map.container == LPB_IMAGE_CONTAINER_UNKNOWN ||
        facts->gain_map.container > LPB_IMAGE_CONTAINER_HEIC ||
        facts->gain_map.representation < LPB_AUX_REPRESENTATION_EMBEDDED ||
        facts->gain_map.representation > LPB_AUX_REPRESENTATION_MATERIALIZED ||
        facts->gain_map.ownership < LPB_AUX_OWNER_PRIMARY ||
        facts->gain_map.ownership > LPB_AUX_OWNER_AUXILIARY ||
        facts->gain_map.item_id == 0 ||
        facts->gain_map.auxiliary_index >= facts->auxiliary_count ||
        !bounded_text_present(facts->gain_map.relationship, sizeof(facts->gain_map.relationship)))
    {
        set_error(context, "[AuthorityViolation] Extraction plan GainMap relationship is incomplete.");
        return false;
    }

    const int32_t expected_owner_role = facts->gain_map.ownership == LPB_AUX_OWNER_PRIMARY
        ? LPB_ARTIFACT_PRIMARY_IMAGE : LPB_ARTIFACT_AUXILIARY_ITEM;
    if (facts->gain_map.owner_artifact_role != expected_owner_role)
    {
        set_error(context, "[AuthorityViolation] Extraction plan GainMap ownership is inconsistent.");
        return false;
    }

    const auto& auxiliary = facts->auxiliary_items[facts->gain_map.auxiliary_index];
    if (!auxiliary.is_present ||
        !bounded_equals_literal(auxiliary.semantic, sizeof(auxiliary.semantic), "GainMap") ||
        auxiliary.container != facts->gain_map.container ||
        auxiliary.representation != facts->gain_map.representation ||
        auxiliary.ownership != facts->gain_map.ownership ||
        auxiliary.item_id != facts->gain_map.item_id ||
        auxiliary.file_range.offset != facts->gain_map.file_range.offset ||
        auxiliary.file_range.length != facts->gain_map.file_range.length ||
        !same_bounded_string(auxiliary.relationship, facts->gain_map.relationship))
    {
        set_error(context, "[AuthorityViolation] Extraction plan GainMap is not bound to its auxiliary relationship.");
        return false;
    }
    return true;
}

bool validate_auxiliary_outputs(
    lpb_context* context,
    const lpb_source_media_facts* facts,
    const lpb_extraction_output* outputs,
    size_t output_count) noexcept
{
    if (output_count > 0 && !outputs) {
        set_error(context, "[AuthorityViolation] Auxiliary output bindings are missing their descriptor array.");
        return false;
    }

    for (size_t i = 0; i < output_count; ++i) {
        const auto& output = outputs[i];
        if (output.struct_size < sizeof(lpb_extraction_output) ||
            output.auxiliary_index >= facts->auxiliary_count ||
            !output.output_path || output.output_path[0] == '\0' ||
            !facts->auxiliary_items[output.auxiliary_index].is_present) {
            set_error(context, "[AuthorityViolation] Auxiliary output binding does not identify a confirmed auxiliary item.");
            return false;
        }
        for (size_t j = 0; j < i; ++j) {
            if (outputs[j].auxiliary_index == output.auxiliary_index ||
                paths_alias(outputs[j].output_path, output.output_path)) {
                set_error(context, "[AuthorityViolation] Auxiliary output bindings contain a duplicate identity or destination.");
                return false;
            }
        }
        const auto& auxiliary = facts->auxiliary_items[output.auxiliary_index];
        if (auxiliary.file_range.length == 0 || auxiliary.container == LPB_IMAGE_CONTAINER_UNKNOWN ||
            auxiliary.representation != LPB_AUX_REPRESENTATION_MATERIALIZED ||
            (facts->gain_map.is_present && output.auxiliary_index == facts->gain_map.auxiliary_index)) {
            set_error(context, "[AuthorityViolation] Auxiliary output binding does not refer to a materialized non-typed auxiliary.");
            return false;
        }
    }

    for (uint32_t i = 0; i < facts->auxiliary_count; ++i) {
        const auto& auxiliary = facts->auxiliary_items[i];
        if (auxiliary.is_present && auxiliary.representation == LPB_AUX_REPRESENTATION_MATERIALIZED &&
            find_auxiliary_output(outputs, output_count, i) == nullptr &&
            !(facts->gain_map.is_present && facts->gain_map.auxiliary_index == i)) {
            set_error(context, "[AuthorityViolation] A materialized auxiliary has no extraction output binding.");
            return false;
        }
    }
    return true;
}

} // namespace

lpb_result extract_source_internal(
    lpb_context* context,
    const char* primary_path,
    const char* secondary_path,
    const lpb_source_media_facts* facts,
    const char* output_image_path,
    const char* output_video_path,
    const char* output_gainmap_path,
    const lpb_file_identity* expected_primary_identity,
    const lpb_file_identity* expected_secondary_identity,
    const lpb_extraction_output* auxiliary_outputs,
    size_t auxiliary_output_count) noexcept
{
    if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
        set_error(context, "[Cancelled] Source extraction cancelled.");
        return LPB_RESULT_CANCELLED;
    }

    if (!primary_path || primary_path[0] == '\0' || !facts) {
        set_error(context, "[InvalidFacts] Invalid arguments for source extraction: primary path and facts are required.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    // ABI and struct size defensive validation
    if (facts->struct_size < sizeof(lpb_source_media_facts)) {
        set_error(context, "[InvalidFacts] facts->struct_size is smaller than expected lpb_source_media_facts size.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (facts->primary_image.struct_size < sizeof(lpb_image_item_facts)) {
        set_error(context, "[InvalidFacts] primary_image struct_size is invalid.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    // Validate Primary Image facts
    if (!facts->primary_image.is_present) {
        set_error(context, "[InvalidFacts] Primary image must be present in source facts.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (facts->primary_image.container == LPB_IMAGE_CONTAINER_UNKNOWN) {
        set_error(context, "[UnsupportedLayout] Primary image container is unknown or unsupported.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (facts->primary_image.file_range.length == 0) {
        set_error(context, "[InvalidFacts] Primary image range length must be greater than zero.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    // Validate Motion Video facts
    if (facts->motion_video.is_present) {
        if (facts->motion_video.struct_size < sizeof(lpb_video_item_facts)) {
            set_error(context, "[InvalidFacts] motion_video struct_size is invalid.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (facts->motion_video.file_range.length == 0) {
            set_error(context, "[InvalidFacts] Motion video range length must be greater than zero when video is present.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (facts->motion_video.container == LPB_VIDEO_CONTAINER_UNKNOWN) {
            set_error(context, "[UnsupportedLayout] Motion video container is unknown or unsupported.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (facts->motion_video.source_index < 0 || facts->motion_video.source_index > 1) {
            set_error(context, "[InvalidFacts] Invalid motion video source index in facts: must be 0 (primary) or 1 (secondary).");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (facts->motion_video.source_index == 1 && (!secondary_path || secondary_path[0] == '\0')) {
            set_error(context, "[InvalidFacts] Motion video requires secondary source file, but none was provided.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
    }

    // Validate GainMap facts
    if (facts->gain_map.is_present) {
        if (facts->gain_map.is_present != 1 ||
            facts->gain_map.struct_size < sizeof(lpb_gainmap_item_facts)) {
            set_error(context, "[InvalidFacts] gain_map struct_size is invalid.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (facts->gain_map.file_range.length == 0) {
            set_error(context, "[InvalidFacts] GainMap range length must be greater than zero when present.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (facts->gain_map.container == LPB_IMAGE_CONTAINER_UNKNOWN) {
            set_error(context, "[UnsupportedLayout] GainMap container is unknown or unsupported.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
    }

    if (!validate_plan_relationships(context, facts) ||
        !validate_auxiliary_outputs(context, facts, auxiliary_outputs, auxiliary_output_count)) {
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }

    const lpb_extraction_output* gainmap_binding = facts->gain_map.is_present
        ? find_auxiliary_output(auxiliary_outputs, auxiliary_output_count, facts->gain_map.auxiliary_index)
        : nullptr;
    if (facts->gain_map.is_present && facts->gain_map.representation == LPB_AUX_REPRESENTATION_MATERIALIZED &&
        (!output_gainmap_path || output_gainmap_path[0] == '\0') && !gainmap_binding) {
        set_error(context, "[AuthorityViolation] Materialized GainMap has no output binding.");
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }
    if (gainmap_binding && output_gainmap_path && output_gainmap_path[0] != '\0') {
        set_error(context, "[AuthorityViolation] GainMap cannot be bound to both the legacy and auxiliary output slots.");
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }

    std::vector<const char*> auxiliary_output_paths;
    auxiliary_output_paths.reserve(auxiliary_output_count);
    for (size_t i = 0; i < auxiliary_output_count; ++i) {
        auxiliary_output_paths.push_back(auxiliary_outputs[i].output_path);
    }

    // Path alias verification across all sources and destinations
    const char* outputs[] = { output_image_path, output_video_path, output_gainmap_path };
    for (int i = 0; i < 3; ++i) {
        if (!outputs[i] || outputs[i][0] == '\0') continue;
        if (paths_alias(primary_path, outputs[i])) {
            set_error(context, "[InvalidAlias] Output path aliases primary source file.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (secondary_path && secondary_path[0] != '\0' && paths_alias(secondary_path, outputs[i])) {
            set_error(context, "[InvalidAlias] Output path aliases secondary source file.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        for (int j = i + 1; j < 3; ++j) {
            if (outputs[j] && outputs[j][0] != '\0' && paths_alias(outputs[i], outputs[j])) {
                set_error(context, "[InvalidAlias] Extraction output paths must not alias each other.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
        }
    }

    for (size_t i = 0; i < auxiliary_output_paths.size(); ++i) {
        const char* output = auxiliary_output_paths[i];
        if (paths_alias(primary_path, output) ||
            (secondary_path && secondary_path[0] != '\0' && paths_alias(secondary_path, output))) {
            set_error(context, "[InvalidAlias] Auxiliary output path aliases a source file.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        for (int j = 0; j < 3; ++j) {
            if (outputs[j] && outputs[j][0] != '\0' && paths_alias(outputs[j], output)) {
                set_error(context, "[InvalidAlias] Auxiliary output path aliases another extraction output.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
        }
        for (size_t j = 0; j < i; ++j) {
            if (paths_alias(auxiliary_output_paths[j], output)) {
                set_error(context, "[InvalidAlias] Auxiliary output paths must be distinct.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
        }
    }

    // Pre-flight check: destinations must not already exist (preserves user files, no overwrite)
    for (int i = 0; i < 3; ++i) {
        if (!outputs[i] || outputs[i][0] == '\0') continue;
        auto p_dst = utf8_to_path(outputs[i]);
        DWORD attrs = GetFileAttributesW(p_dst.c_str());
        if (attrs != INVALID_FILE_ATTRIBUTES || path_is_reparse_point(p_dst)) {
            std::string msg = "[OutputPublishFailed] Destination path already exists and will not be overwritten: " +
                std::string(outputs[i]) + ".";
            set_error(context, msg.c_str());
            return LPB_RESULT_INVALID_ARGUMENT;
        }
    }

    // Open source handles in strictly read-only mode with FILE_SHARE_READ (prevents concurrent mutation/deletion)
    auto p_prim = utf8_to_path(primary_path);
    handle_guard primary_guard;
    primary_guard.h = CreateFileW(
        p_prim.c_str(),
        GENERIC_READ,
        FILE_SHARE_READ,
        NULL,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN,
        NULL);

    if (primary_guard.h == INVALID_HANDLE_VALUE) {
        DWORD err = GetLastError();
        std::string msg = "[SourceRangeUnreadable] Failed to open primary source file: " +
            std::string(primary_path) + " (Win32 error: " + std::to_string(err) + ").";
        set_error(context, msg.c_str());
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    handle_guard secondary_guard;
    bool need_secondary = (facts->motion_video.is_present && facts->motion_video.source_index == 1 &&
                           output_video_path && output_video_path[0] != '\0');
    for (uint32_t i = 0; i < facts->auxiliary_count; ++i) {
        if (facts->auxiliary_items[i].is_present && facts->auxiliary_items[i].source_index == 1) {
            need_secondary = true;
        }
    }
    if (need_secondary && (!secondary_path || secondary_path[0] == '\0')) {
        set_error(context, "[InvalidFacts] Auxiliary relationship requires a secondary source file, but none was provided.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const bool validate_secondary_identity = expected_secondary_identity != nullptr;
    if (need_secondary || validate_secondary_identity) {
        auto p_sec = utf8_to_path(secondary_path);
        secondary_guard.h = CreateFileW(
            p_sec.c_str(),
            GENERIC_READ,
            FILE_SHARE_READ,
            NULL,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN,
            NULL);

        if (secondary_guard.h == INVALID_HANDLE_VALUE) {
            DWORD err = GetLastError();
            std::string msg = "[SourceRangeUnreadable] Failed to open secondary source file: " +
                std::string(secondary_path) + " (Win32 error: " + std::to_string(err) + ").";
            set_error(context, msg.c_str());
            return LPB_RESULT_INVALID_ARGUMENT;
        }
    }

    for (const char* output : auxiliary_output_paths) {
        auto p_dst = utf8_to_path(output);
        DWORD attrs = GetFileAttributesW(p_dst.c_str());
        if (attrs != INVALID_FILE_ATTRIBUTES || path_is_reparse_point(p_dst)) {
            std::string msg = "[OutputPublishFailed] Destination path already exists and will not be overwritten: " +
                std::string(output) + ".";
            set_error(context, msg.c_str());
            return LPB_RESULT_INVALID_ARGUMENT;
        }
    }

    // When invoked by the production plan path, bind the actual handles at
    // the extraction site as well as during plan issuance.  This closes the
    // path-replacement window between authority validation and first slicing;
    // the SHA check below remains the content-integrity check.
    if (expected_primary_identity != nullptr) {
        lpb_file_identity actual_primary_identity{};
        std::wstring actual_primary_path;
        if (!capture_file_identity_from_handle(
                primary_guard.h, actual_primary_identity, actual_primary_path) ||
            !same_file_identity(actual_primary_identity, *expected_primary_identity)) {
            set_error(context, "[SourceChanged] Primary source file identity changed after inspection.");
            return LPB_RESULT_SOURCE_CHANGED;
        }
    }
    if (expected_secondary_identity != nullptr) {
        lpb_file_identity actual_secondary_identity{};
        std::wstring actual_secondary_path;
        if (!validate_secondary_identity ||
            !capture_file_identity_from_handle(
                secondary_guard.h, actual_secondary_identity, actual_secondary_path) ||
            !same_file_identity(actual_secondary_identity, *expected_secondary_identity)) {
            set_error(context, "[SourceChanged] Secondary source file identity changed after inspection.");
            return LPB_RESULT_SOURCE_CHANGED;
        }
    }

    // Verify source snapshot identity against open handles
    bool has_expected_prim_sha = false;
    for (int i = 0; i < 32; ++i) {
        if (facts->primary_sha256[i] != 0) {
            has_expected_prim_sha = true;
            break;
        }
    }
    if (!has_expected_prim_sha) {
        set_error(context, "[InvalidFacts] Primary source snapshot SHA-256 is required and must not be all zero.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    uint8_t actual_prim_sha[32]{};
    if (!lpb::crypto::sha256_file(primary_guard.h, actual_prim_sha)) {
        DWORD err = GetLastError();
        std::string msg = "[SourceRangeUnreadable] Failed to compute SHA-256 snapshot for primary source file (Win32 error: " +
            std::to_string(err) + ").";
        set_error(context, msg.c_str());
        return LPB_RESULT_INTERNAL_ERROR;
    }
    if (std::memcmp(actual_prim_sha, facts->primary_sha256, 32) != 0) {
        set_error(context, "[SourceChanged] Primary source file content does not match inspected snapshot identity.");
        return expected_primary_identity != nullptr
            ? LPB_RESULT_SOURCE_CHANGED
            : LPB_RESULT_INVALID_ARGUMENT;
    }

    if (need_secondary) {
        bool has_expected_sec_sha = false;
        for (int i = 0; i < 32; ++i) {
            if (facts->secondary_sha256[i] != 0) {
                has_expected_sec_sha = true;
                break;
            }
        }
        if (!has_expected_sec_sha) {
            set_error(context, "[InvalidFacts] Secondary source snapshot SHA-256 is required when secondary source is needed.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        uint8_t actual_sec_sha[32]{};
        if (!lpb::crypto::sha256_file(secondary_guard.h, actual_sec_sha)) {
            DWORD err = GetLastError();
            std::string msg = "[SourceRangeUnreadable] Failed to compute SHA-256 snapshot for secondary source file (Win32 error: " +
                std::to_string(err) + ").";
            set_error(context, msg.c_str());
            return LPB_RESULT_INTERNAL_ERROR;
        }
        if (std::memcmp(actual_sec_sha, facts->secondary_sha256, 32) != 0) {
            set_error(context, "[SourceChanged] Secondary source file content does not match inspected snapshot identity.");
            return expected_secondary_identity != nullptr
                ? LPB_RESULT_SOURCE_CHANGED
                : LPB_RESULT_INVALID_ARGUMENT;
        }
    } else if (facts->has_secondary_source != 0 && secondary_guard.h != INVALID_HANDLE_VALUE) {
        bool has_expected_sec_sha = false;
        for (int i = 0; i < 32; ++i) {
            if (facts->secondary_sha256[i] != 0) {
                has_expected_sec_sha = true;
                break;
            }
        }
        if (has_expected_sec_sha) {
            uint8_t actual_sec_sha[32]{};
            if (!lpb::crypto::sha256_file(secondary_guard.h, actual_sec_sha)) {
                DWORD err = GetLastError();
                std::string msg = "[SourceRangeUnreadable] Failed to compute SHA-256 snapshot for secondary source file (Win32 error: " +
                    std::to_string(err) + ").";
                set_error(context, msg.c_str());
                return LPB_RESULT_INTERNAL_ERROR;
            }
            if (std::memcmp(actual_sec_sha, facts->secondary_sha256, 32) != 0) {
                set_error(context, "[SourceChanged] Secondary source file content does not match inspected snapshot identity.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
        }
    }

    // Plan tasks
    std::vector<slice_task> tasks;
    tasks.reserve(3 + facts->auxiliary_count);

    // 1. Primary Image
    if (output_image_path && output_image_path[0] != '\0') {
        slice_task t{};
        t.src_handle = primary_guard.h;
        t.src_path = primary_path;
        t.offset = facts->primary_image.file_range.offset;
        t.length = facts->primary_image.file_range.length;
        t.dst_path = output_image_path;
        t.target_artifact = 0;
        t.artifact_name = "PrimaryImage";
        tasks.push_back(t);
    }

    // 2. Motion Video
    if (facts->motion_video.is_present && output_video_path && output_video_path[0] != '\0') {
        slice_task t{};
        if (facts->motion_video.source_index == 1) {
            t.src_handle = secondary_guard.h;
            t.src_path = secondary_path;
        } else {
            t.src_handle = primary_guard.h;
            t.src_path = primary_path;
        }
        t.offset = facts->motion_video.file_range.offset;
        t.length = facts->motion_video.file_range.length;
        t.dst_path = output_video_path;
        t.target_artifact = 1;
        t.artifact_name = "MotionVideo";
        tasks.push_back(t);
    }

    // 3. Gain Map
    const char* effective_gainmap_output = output_gainmap_path;
    if (!effective_gainmap_output && gainmap_binding) effective_gainmap_output = gainmap_binding->output_path;
    if (facts->gain_map.is_present && effective_gainmap_output && effective_gainmap_output[0] != '\0') {
        slice_task t{};
        const auto& auxiliary = facts->auxiliary_items[facts->gain_map.auxiliary_index];
        t.src_handle = auxiliary.source_index == 1 ? secondary_guard.h : primary_guard.h;
        t.src_path = auxiliary.source_index == 1 ? secondary_path : primary_path;
        t.offset = facts->gain_map.file_range.offset;
        t.length = facts->gain_map.file_range.length;
        t.dst_path = effective_gainmap_output;
        t.target_artifact = 2;
        t.artifact_name = "GainMap";
        tasks.push_back(t);
    }

    // Generic confirmed auxiliary outputs.  The output array is only a
    // materialization request; all source identity and relationship facts
    // still come from the claimed Inspector plan.
    for (size_t binding_index = 0; binding_index < auxiliary_output_count; ++binding_index) {
        const auto& binding = auxiliary_outputs[binding_index];
        if (facts->gain_map.is_present && binding.auxiliary_index == facts->gain_map.auxiliary_index) {
            continue; // already represented by the typed GainMap task above
        }
        const auto& auxiliary = facts->auxiliary_items[binding.auxiliary_index];
        slice_task t{};
        t.src_handle = auxiliary.source_index == 1 ? secondary_guard.h : primary_guard.h;
        t.src_path = auxiliary.source_index == 1 ? secondary_path : primary_path;
        t.offset = auxiliary.file_range.offset;
        t.length = auxiliary.file_range.length;
        t.dst_path = binding.output_path;
        t.target_artifact = static_cast<int32_t>(3u + binding.auxiliary_index);
        t.artifact_name = auxiliary.semantic[0] != '\0' ? auxiliary.semantic : "AuxiliaryItem";
        tasks.push_back(t);
    }

    if (tasks.empty()) {
        return LPB_RESULT_OK;
    }

    // Verify ranges against source handle lengths
    for (auto& task : tasks) {
        LARGE_INTEGER fsize{};
        if (!GetFileSizeEx(task.src_handle, &fsize)) {
            set_error(context, "[SourceRangeUnreadable] Failed to query source file size.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        const uint64_t source_size = static_cast<uint64_t>(fsize.QuadPart);
        if (task.offset > source_size || task.length > source_size - task.offset) {
            std::string msg = "[InvalidFacts] Requested " + std::string(task.artifact_name) +
                " range is outside the source file boundary: offset=" + std::to_string(task.offset) +
                ", length=" + std::to_string(task.length) + ", fileSize=" + std::to_string(source_size) + ".";
            set_error(context, msg.c_str());
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        auto p_dst = utf8_to_path(task.dst_path);
        std::error_code ec;
        task.final_dst_path = std::filesystem::absolute(p_dst, ec).lexically_normal();
        if (ec) {
            task.final_dst_path = p_dst;
        }

        auto temp_dir = task.final_dst_path.parent_path();
        ec.clear();
        if (temp_dir.empty()) {
            temp_dir = std::filesystem::current_path(ec);
        }
        if (ec || !std::filesystem::exists(temp_dir, ec) || !std::filesystem::is_directory(temp_dir, ec)) {
            std::string msg = "[OutputPublishFailed] Destination directory does not exist or is invalid for " +
                std::string(task.artifact_name) + ".";
            set_error(context, msg.c_str());
            return LPB_RESULT_INVALID_ARGUMENT;
        }
    }

    // Staging and publication tracking.  Each temp file remains open from
    // CREATE_NEW through post-publish verification.  The handle, not a path,
    // is the transaction's ownership token.
    for (auto& task : tasks) {
        if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
            return rollback_extraction_transaction(
                context,
                {LPB_RESULT_CANCELLED, "[Cancelled]", "Source extraction cancelled."},
                tasks);
        }

        const auto temp_dir = task.final_dst_path.parent_path();
        if (path_is_reparse_point(temp_dir)) {
            return rollback_extraction_transaction(
                context,
                {LPB_RESULT_INTERNAL_ERROR, "[OutputPublishFailed]", "Destination directory is a reparse point for " + std::string(task.artifact_name) + "."},
                tasks);
        }

        if (!open_destination_directory(temp_dir, task.destination_directory_handle)) {
            const DWORD err = GetLastError();
            return rollback_extraction_transaction(
                context,
                {LPB_RESULT_INTERNAL_ERROR, "[OutputPublishFailed]", "Destination directory identity could not be pinned for " + std::string(task.artifact_name) + " (Win32 error: " + std::to_string(err) + ")."},
                tasks);
        }

        if (!open_unique_temp_file(temp_dir, task.temp_path, task.temp_handle)) {
            const DWORD err = GetLastError();
            const std::string cat = (err == ERROR_DISK_FULL || err == ERROR_HANDLE_DISK_FULL)
                ? "[DiskFull]" : "[OutputPublishFailed]";
            const std::string det = (cat == "[DiskFull]")
                ? ("Disk full while creating temporary file for " + std::string(task.artifact_name) + " (Win32 error: " + std::to_string(err) + ").")
                : ("Failed to create exclusive temporary file for " + std::string(task.artifact_name) + " (Win32 error: " + std::to_string(err) + ").");
            return rollback_extraction_transaction(context, {LPB_RESULT_INTERNAL_ERROR, cat, det}, tasks);
        }

        std::wstring temp_final_path;
        if (!capture_file_identity_from_handle(task.temp_handle, task.temp_identity, temp_final_path) ||
            task.temp_identity.file_size != 0 || task.temp_identity.link_count != 1) {
            return rollback_extraction_transaction(
                context,
                {LPB_RESULT_INTERNAL_ERROR, "[OutputPublishFailed]", "Temporary file identity was not stable immediately after exclusive creation for " + std::string(task.artifact_name) + "."},
                tasks);
        }

        LARGE_INTEGER seek_pos{};
        seek_pos.QuadPart = static_cast<LONGLONG>(task.offset);
        if (!SetFilePointerEx(task.src_handle, seek_pos, NULL, FILE_BEGIN)) {
            return rollback_extraction_transaction(
                context,
                {LPB_RESULT_INVALID_ARGUMENT, "[SourceRangeUnreadable]", "Failed to seek to slice offset in source file for " + std::string(task.artifact_name) + "."},
                tasks);
        }

        constexpr size_t buffer_size = 64 * 1024;
        std::vector<uint8_t> buffer(buffer_size);
        lpb::crypto::sha256_ctx source_slice_sha;
        uint64_t remaining = task.length;
        uint64_t bytes_written = 0;

        while (remaining > 0) {
            if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
                return rollback_extraction_transaction(
                    context,
                    {LPB_RESULT_CANCELLED, "[Cancelled]", "Source extraction cancelled."},
                    tasks);
            }

            const DWORD to_read = static_cast<DWORD>(std::min<uint64_t>(remaining, buffer_size));
            DWORD bytes_read = 0;
            const uint32_t base_fault = context
                ? (static_cast<uint32_t>(context->extractor_hook.fault) & 0x7F) : 0;

            if (base_fault == LPB_EXTRACTOR_FAULT_SHORT_READ &&
                context->extractor_hook.target_artifact == task.target_artifact &&
                bytes_written + to_read >= context->extractor_hook.trigger_after_bytes) {
                return rollback_extraction_transaction(
                    context,
                    {LPB_RESULT_INTERNAL_ERROR, "[SourceRangeUnreadable]", "Injected short read for " + std::string(task.artifact_name) + "."},
                    tasks);
            }

            if (!ReadFile(task.src_handle, buffer.data(), to_read, &bytes_read, NULL) || bytes_read == 0) {
                return rollback_extraction_transaction(
                    context,
                    {LPB_RESULT_INTERNAL_ERROR, "[SourceRangeUnreadable]", "Unexpected EOF or read error while extracting slice for " + std::string(task.artifact_name) + "."},
                    tasks);
            }
            source_slice_sha.update(buffer.data(), bytes_read);

            BOOL write_ok = TRUE;
            DWORD bytes_written_chunk = 0;
            const bool inject_write_fault = context &&
                context->extractor_hook.target_artifact == task.target_artifact &&
                bytes_written + bytes_read >= context->extractor_hook.trigger_after_bytes;
            if (inject_write_fault && base_fault == LPB_EXTRACTOR_FAULT_DISK_FULL) {
                SetLastError(ERROR_DISK_FULL);
                write_ok = FALSE;
            } else if (inject_write_fault && base_fault == LPB_EXTRACTOR_FAULT_WRITE_FAIL) {
                SetLastError(ERROR_WRITE_FAULT);
                write_ok = FALSE;
            } else {
                write_ok = WriteFile(task.temp_handle, buffer.data(), bytes_read, &bytes_written_chunk, NULL);
            }

            if (!write_ok || bytes_written_chunk != bytes_read) {
                const DWORD err = GetLastError();
                const std::string cat = (err == ERROR_DISK_FULL || err == ERROR_HANDLE_DISK_FULL)
                    ? "[DiskFull]" : "[OutputWriteFailed]";
                const std::string det = (cat == "[DiskFull]")
                    ? ("Disk full while writing extracted slice for " + std::string(task.artifact_name) + " (Win32 error: " + std::to_string(err) + ").")
                    : ("Failed while writing extracted slice for " + std::string(task.artifact_name) + " (Win32 error: " + std::to_string(err) + ").");
                return rollback_extraction_transaction(context, {LPB_RESULT_INTERNAL_ERROR, cat, det}, tasks);
            }

            remaining -= bytes_read;
            bytes_written += bytes_read;

            // The publish-barrier hooks are deliberately invoked after the
            // flush below, not from this per-chunk callback.
            if (context && context->extractor_hook.step_callback &&
                context->extractor_hook.target_artifact == task.target_artifact &&
                base_fault != LPB_EXTRACTOR_FAULT_TEMP_PUBLISH_BARRIER &&
                base_fault != LPB_EXTRACTOR_FAULT_POST_PUBLISH_BARRIER) {
                context->extractor_hook.step_callback(
                    context->extractor_hook.callback_user_data,
                    task.target_artifact,
                    bytes_written);
                if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
                    return rollback_extraction_transaction(
                        context,
                        {LPB_RESULT_CANCELLED, "[Cancelled]", "Source extraction cancelled."},
                        tasks);
                }
            }
        }

        source_slice_sha.finalize(task.expected_slice_sha256);

        BOOL flush_ok = FlushFileBuffers(task.temp_handle);
        const uint32_t flush_base_fault = context
            ? (static_cast<uint32_t>(context->extractor_hook.fault) & 0x7F) : 0;
        if (flush_base_fault == LPB_EXTRACTOR_FAULT_FLUSH_DISK_FULL &&
            context->extractor_hook.target_artifact == task.target_artifact) {
            SetLastError(ERROR_DISK_FULL);
            flush_ok = FALSE;
        } else if (flush_base_fault == LPB_EXTRACTOR_FAULT_FLUSH_WRITE_FAIL &&
                   context->extractor_hook.target_artifact == task.target_artifact) {
            SetLastError(ERROR_WRITE_FAULT);
            flush_ok = FALSE;
        }

        if (!flush_ok) {
            const DWORD err = GetLastError();
            const std::string cat = (err == ERROR_DISK_FULL || err == ERROR_HANDLE_DISK_FULL)
                ? "[DiskFull]" : "[OutputWriteFailed]";
            const std::string det = (cat == "[DiskFull]")
                ? ("Disk full while flushing extracted slice for " + std::string(task.artifact_name) + " (Win32 error: " + std::to_string(err) + ").")
                : ("Failed while flushing extracted slice for " + std::string(task.artifact_name) + " (Win32 error: " + std::to_string(err) + ").");
            return rollback_extraction_transaction(context, {LPB_RESULT_INTERNAL_ERROR, cat, det}, tasks);
        }

        lpb_file_identity staged_identity{};
        std::wstring staged_path;
        if (!capture_file_identity_from_handle(task.temp_handle, staged_identity, staged_path) ||
            !same_object_identity(staged_identity, task.temp_identity) ||
            staged_identity.file_size != task.length || staged_identity.link_count != 1 ||
            !path_matches_handle(task.temp_handle, task.temp_path, task.temp_identity) ||
            !lpb::crypto::sha256_file(task.temp_handle, task.staged_sha256) ||
            std::memcmp(task.staged_sha256, task.expected_slice_sha256, 32) != 0) {
            return rollback_extraction_transaction(
                context,
                {LPB_RESULT_INTERNAL_ERROR, "[OutputPublishFailed]", "Temporary artifact identity or hash did not match the streamed source slice for " + std::string(task.artifact_name) + "."},
                tasks);
        }

        const uint32_t barrier_fault = context
            ? (static_cast<uint32_t>(context->extractor_hook.fault) & 0x7F) : 0;
        if (barrier_fault == LPB_EXTRACTOR_FAULT_TEMP_PUBLISH_BARRIER &&
            context->extractor_hook.target_artifact == task.target_artifact &&
            context->extractor_hook.step_callback) {
            context->extractor_hook.step_callback(
                context->extractor_hook.callback_user_data,
                task.target_artifact,
                bytes_written);
            if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
                return rollback_extraction_transaction(
                    context,
                    {LPB_RESULT_CANCELLED, "[Cancelled]", "Source extraction cancelled."},
                    tasks);
            }
        }
    }

    // Publication phase: SetFileInformationByHandle renames the still-open
    // owned file object.  No close-then-path-rename window exists.
    for (auto& task : tasks) {
        if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
            return rollback_extraction_transaction(
                context,
                {LPB_RESULT_CANCELLED, "[Cancelled]", "Source extraction cancelled."},
                tasks);
        }

        const DWORD destination_attributes = GetFileAttributesW(task.final_dst_path.c_str());
        if (destination_attributes != INVALID_FILE_ATTRIBUTES || path_is_reparse_point(task.final_dst_path)) {
            return rollback_extraction_transaction(
                context,
                {LPB_RESULT_INTERNAL_ERROR, "[OutputPublishFailed]", "Destination appeared, changed, or is a reparse point before handle publish for " + std::string(task.artifact_name) + "."},
                tasks);
        }

        const uint32_t pub_base_fault = context
            ? (static_cast<uint32_t>(context->extractor_hook.fault) & 0x7F) : 0;
        if (pub_base_fault == LPB_EXTRACTOR_FAULT_PUBLISH_FAIL &&
            context->extractor_hook.target_artifact == task.target_artifact) {
            return rollback_extraction_transaction(
                context,
                {LPB_RESULT_INTERNAL_ERROR, "[OutputPublishFailed]", "Injected publish failure for " + std::string(task.artifact_name) + "."},
                tasks);
        }

        if (!path_matches_handle(task.temp_handle, task.temp_path, task.temp_identity)) {
            return rollback_extraction_transaction(
                context,
                {LPB_RESULT_INTERNAL_ERROR, "[OutputPublishFailed]", "Temporary artifact identity changed before handle publish for " + std::string(task.artifact_name) + "."},
                tasks);
        }

        const std::wstring final_name = task.final_dst_path.wstring();
        if (final_name.empty() || task.destination_directory_handle == INVALID_HANDLE_VALUE) {
            return rollback_extraction_transaction(
                context,
                {LPB_RESULT_INTERNAL_ERROR, "[OutputPublishFailed]", "Destination filename or pinned directory handle is invalid for " + std::string(task.artifact_name) + "."},
                tasks);
        }
        const size_t rename_size = sizeof(FILE_RENAME_INFO) +
            (final_name.size() == 0 ? 0 : (final_name.size() - 1) * sizeof(wchar_t));
        std::vector<uint8_t> rename_buffer(rename_size);
        auto* rename_info = reinterpret_cast<FILE_RENAME_INFO*>(rename_buffer.data());
        rename_info->ReplaceIfExists = FALSE;
        // The parent directory is pinned open without FILE_SHARE_DELETE
        // above; use the absolute final name because some Windows filesystems
        // reject a RootDirectory handle in FILE_RENAME_INFO with ERROR_INVALID_PARAMETER.
        rename_info->RootDirectory = nullptr;
        rename_info->FileNameLength = static_cast<DWORD>(final_name.size() * sizeof(wchar_t));
        std::memcpy(rename_info->FileName, final_name.data(), rename_info->FileNameLength);

        if (!SetFileInformationByHandle(
                task.temp_handle,
                FileRenameInfo,
                rename_info,
                static_cast<DWORD>(rename_buffer.size()))) {
            const DWORD win_err = GetLastError();
            const std::string cat = (win_err == ERROR_DISK_FULL || win_err == ERROR_HANDLE_DISK_FULL)
                ? "[DiskFull]" : "[OutputPublishFailed]";
            const std::string det = (win_err == ERROR_FILE_EXISTS || win_err == ERROR_ALREADY_EXISTS)
                ? ("Destination file already exists: " + path_to_utf8(task.final_dst_path) + ".")
                : ("Failed to publish the owned extraction handle for " + std::string(task.artifact_name) + " (Win32 error: " + std::to_string(win_err) + ").");
            return rollback_extraction_transaction(context, {LPB_RESULT_INTERNAL_ERROR, cat, det}, tasks);
        }
        task.published = true;

        const uint32_t post_barrier_fault = context
            ? (static_cast<uint32_t>(context->extractor_hook.fault) & 0x7F) : 0;
        if (post_barrier_fault == LPB_EXTRACTOR_FAULT_POST_PUBLISH_BARRIER &&
            context->extractor_hook.target_artifact == task.target_artifact &&
            context->extractor_hook.step_callback) {
            context->extractor_hook.step_callback(
                context->extractor_hook.callback_user_data,
                task.target_artifact,
                task.length);
            if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
                return rollback_extraction_transaction(
                    context,
                    {LPB_RESULT_CANCELLED, "[Cancelled]", "Source extraction cancelled."},
                    tasks);
            }
        }

        lpb_file_identity final_identity{};
        std::wstring final_path;
        uint8_t final_hash[32]{};
        if (!capture_file_identity_from_handle(task.temp_handle, final_identity, final_path) ||
            !same_object_identity(final_identity, task.temp_identity) ||
            final_identity.file_size != task.length || final_identity.link_count != 1 ||
            !path_matches_handle(task.temp_handle, task.final_dst_path, task.temp_identity) ||
            !lpb::crypto::sha256_file(task.temp_handle, final_hash) ||
            std::memcmp(final_hash, task.expected_slice_sha256, 32) != 0) {
            return rollback_extraction_transaction(
                context,
                {LPB_RESULT_INTERNAL_ERROR, "[OutputPublishFailed]", "Published artifact identity, length, or hash did not match the streamed source slice for " + std::string(task.artifact_name) + "."},
                tasks);
        }
    }

    for (auto& task : tasks) {
        if (task.temp_handle != INVALID_HANDLE_VALUE && task.temp_handle != NULL) {
            CloseHandle(task.temp_handle);
            task.temp_handle = INVALID_HANDLE_VALUE;
        }
        if (task.destination_directory_handle != INVALID_HANDLE_VALUE &&
            task.destination_directory_handle != NULL) {
            CloseHandle(task.destination_directory_handle);
            task.destination_directory_handle = INVALID_HANDLE_VALUE;
        }
    }
    return LPB_RESULT_OK;
}

lpb_result extract_source_with_plan(
    lpb_context* context,
    lpb_extraction_plan* plan,
    const char* primary_path,
    const char* secondary_path,
    const char* output_image_path,
    const char* output_video_path,
    const char* output_gainmap_path,
    const lpb_extraction_output* auxiliary_outputs,
    size_t auxiliary_output_count) noexcept
{
    if (context == nullptr)
    {
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }

    lpb_context_operation context_operation(context);
    if (!context_operation.acquired())
    {
        set_error(context, "[AuthorityViolation] Native context is being destroyed.");
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }

    lpb_plan_snapshot snapshot{};
    const lpb_result claim_result = begin_plan_native_call(context, plan, snapshot);
    if (claim_result != LPB_RESULT_OK)
    {
        return claim_result;
    }

    struct plan_attempt_guard
    {
        lpb_context* context;
        uint64_t token;
        ~plan_attempt_guard() noexcept { finish_plan_attempt(context, token); }
    } attempt{context, plan_token_from_handle(plan)};

    try
    {
        const lpb_source_media_facts& facts = snapshot.facts;
        const bool has_secondary = snapshot.has_secondary;

        // The native claim intentionally precedes this argument check.  An
        // invalid primary path is still a single extraction attempt and the
        // plan is consumed by the guard below.
        if (primary_path == nullptr || primary_path[0] == '\0')
        {
            set_error(context, "[InvalidArgument] Primary source path is required.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        const bool supplied_secondary = secondary_path != nullptr && secondary_path[0] != '\0';
        if (supplied_secondary != has_secondary)
        {
            set_error(context, "[AuthorityViolation] Extraction paths do not match the Inspector-issued source pair.");
            return LPB_RESULT_AUTHORITY_VIOLATION;
        }

        lpb_file_identity actual_primary_identity{};
        std::wstring actual_primary_path;
        if (!capture_file_identity(primary_path, actual_primary_identity, actual_primary_path) ||
            !same_file_identity(actual_primary_identity, snapshot.primary_identity) ||
            _wcsicmp(actual_primary_path.c_str(), snapshot.primary_final_path.c_str()) != 0)
        {
            set_error(context, "[SourceChanged] Primary source path or file identity no longer matches the issued extraction plan.");
            return LPB_RESULT_SOURCE_CHANGED;
        }

        if (has_secondary)
        {
            lpb_file_identity actual_secondary_identity{};
            std::wstring actual_secondary_path;
            if (!capture_file_identity(secondary_path, actual_secondary_identity, actual_secondary_path) ||
                !same_file_identity(actual_secondary_identity, snapshot.secondary_identity) ||
                _wcsicmp(actual_secondary_path.c_str(), snapshot.secondary_final_path.c_str()) != 0)
            {
                set_error(context, "[SourceChanged] Secondary source path or file identity no longer matches the issued extraction plan.");
                return LPB_RESULT_SOURCE_CHANGED;
            }
        }

        // Revalidate the complete Inspector-owned relationship graph at the
        // mutation boundary.  No caller-provided facts reach this function.
        if (!validate_plan_relationships(context, &facts))
        {
            return LPB_RESULT_AUTHORITY_VIOLATION;
        }

        return extract_source_internal(
            context, primary_path, secondary_path, &facts,
            output_image_path, output_video_path, output_gainmap_path,
            &snapshot.primary_identity,
            has_secondary ? &snapshot.secondary_identity : nullptr,
            auxiliary_outputs,
            auxiliary_output_count);
    }
    catch (const std::exception& ex)
    {
        set_error(context, ex.what());
        return LPB_RESULT_INTERNAL_ERROR;
    }
    catch (...)
    {
        set_error(context, "[InternalError] Native extraction failed unexpectedly.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
}

} // namespace lpb::media
