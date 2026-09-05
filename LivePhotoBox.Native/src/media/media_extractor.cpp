#include "media/media_extractor.h"
#include "foundation/internal.h"
#include "foundation/sha256.h"
#include <filesystem>
#include <Windows.h>
#include <vector>
#include <string>
#include <algorithm>
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
    int32_t target_artifact{0}; // 0 = PrimaryImage, 1 = MotionVideo, 2 = GainMap
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
    const std::vector<slice_task>& tasks,
    const std::vector<std::filesystem::path>& published_paths) noexcept
{
    std::string cleanup_fail_path;
    DWORD cleanup_fail_err = 0;
    bool all_clean = true;

    const bool fail_cleanup = context &&
        (((static_cast<uint32_t>(context->extractor_hook.fault) & 0x80) != 0) ||
         context->extractor_hook.fault == LPB_EXTRACTOR_FAULT_CLEANUP_FAIL);

    for (const auto& path : published_paths) {
        if (fail_cleanup) {
            all_clean = false;
            if (cleanup_fail_path.empty()) {
                cleanup_fail_path = path_to_utf8(path);
                cleanup_fail_err = ERROR_ACCESS_DENIED;
            }
            continue;
        }

        if (!DeleteFileW(path.c_str())) {
            DWORD err = GetLastError();
            if (err != ERROR_FILE_NOT_FOUND) {
                all_clean = false;
                if (cleanup_fail_path.empty()) {
                    cleanup_fail_path = path_to_utf8(path);
                    cleanup_fail_err = err;
                }
            }
        }
    }

    for (const auto& task : tasks) {
        if (!task.temp_path.empty()) {
            if (fail_cleanup) {
                all_clean = false;
                if (cleanup_fail_path.empty()) {
                    cleanup_fail_path = path_to_utf8(task.temp_path);
                    cleanup_fail_err = ERROR_ACCESS_DENIED;
                }
                continue;
            }

            if (!DeleteFileW(task.temp_path.c_str())) {
                DWORD err = GetLastError();
                if (err != ERROR_FILE_NOT_FOUND) {
                    all_clean = false;
                    if (cleanup_fail_path.empty()) {
                        cleanup_fail_path = path_to_utf8(task.temp_path);
                        cleanup_fail_err = err;
                    }
                }
            }
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

} // namespace

lpb_result extract_source(
    lpb_context* context,
    const char* primary_path,
    const char* secondary_path,
    const lpb_source_media_facts* facts,
    const char* output_image_path,
    const char* output_video_path,
    const char* output_gainmap_path) noexcept
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
        if (facts->gain_map.struct_size < sizeof(lpb_gainmap_item_facts)) {
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

    // Pre-flight check: destinations must not already exist (preserves user files, no overwrite)
    for (int i = 0; i < 3; ++i) {
        if (!outputs[i] || outputs[i][0] == '\0') continue;
        auto p_dst = utf8_to_path(outputs[i]);
        DWORD attrs = GetFileAttributesW(p_dst.c_str());
        if (attrs != INVALID_FILE_ATTRIBUTES) {
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
    const bool need_secondary = (facts->motion_video.is_present && facts->motion_video.source_index == 1 &&
                                 output_video_path && output_video_path[0] != '\0');
    if (need_secondary) {
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
        return LPB_RESULT_INVALID_ARGUMENT;
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
            return LPB_RESULT_INVALID_ARGUMENT;
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
    tasks.reserve(3);

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
    if (facts->gain_map.is_present && output_gainmap_path && output_gainmap_path[0] != '\0') {
        slice_task t{};
        t.src_handle = primary_guard.h;
        t.src_path = primary_path;
        t.offset = facts->gain_map.file_range.offset;
        t.length = facts->gain_map.file_range.length;
        t.dst_path = output_gainmap_path;
        t.target_artifact = 2;
        t.artifact_name = "GainMap";
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
        task.final_dst_path = p_dst;

        auto temp_dir = p_dst.parent_path();
        std::error_code ec;
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

    // Staging and publication tracking
    std::vector<std::filesystem::path> published_paths;
    published_paths.reserve(tasks.size());

    // Stage all tasks into temporary files
    for (auto& task : tasks) {
        if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
            return rollback_extraction_transaction(
                context,
                {LPB_RESULT_CANCELLED, "[Cancelled]", "Source extraction cancelled."},
                tasks,
                published_paths);
        }

        auto temp_dir = task.final_dst_path.parent_path();
        wchar_t temp_name[MAX_PATH]{};
        if (GetTempFileNameW(temp_dir.c_str(), L"lpb", 0, temp_name) == 0) {
            return rollback_extraction_transaction(
                context,
                {LPB_RESULT_INTERNAL_ERROR, "[OutputPublishFailed]", "Failed to allocate temporary extraction file for " + std::string(task.artifact_name) + "."},
                tasks,
                published_paths);
        }
        task.temp_path = temp_name;

        HANDLE h_temp = CreateFileW(
            task.temp_path.c_str(),
            GENERIC_WRITE,
            0,
            NULL,
            CREATE_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            NULL);

        if (h_temp == INVALID_HANDLE_VALUE) {
            DWORD err = GetLastError();
            std::string cat = (err == ERROR_DISK_FULL || err == ERROR_HANDLE_DISK_FULL) ? "[DiskFull]" : "[OutputWriteFailed]";
            std::string det = (cat == "[DiskFull]")
                ? ("Disk full while creating temporary file for " + std::string(task.artifact_name) + " (Win32 error: 112).")
                : ("Failed to open temporary file for writing " + std::string(task.artifact_name) + " (Win32 error: " + std::to_string(err) + ").");
            return rollback_extraction_transaction(
                context,
                {LPB_RESULT_INTERNAL_ERROR, cat, det},
                tasks,
                published_paths);
        }

        LARGE_INTEGER seek_pos{};
        seek_pos.QuadPart = static_cast<LONGLONG>(task.offset);
        if (!SetFilePointerEx(task.src_handle, seek_pos, NULL, FILE_BEGIN)) {
            CloseHandle(h_temp);
            return rollback_extraction_transaction(
                context,
                {LPB_RESULT_INVALID_ARGUMENT, "[SourceRangeUnreadable]", "Failed to seek to slice offset in source file for " + std::string(task.artifact_name) + "."},
                tasks,
                published_paths);
        }

        constexpr size_t buffer_size = 64 * 1024;
        std::vector<uint8_t> buffer(buffer_size);
        uint64_t remaining = task.length;
        uint64_t bytes_written = 0;

        while (remaining > 0) {
            if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
                CloseHandle(h_temp);
                return rollback_extraction_transaction(
                    context,
                    {LPB_RESULT_CANCELLED, "[Cancelled]", "Source extraction cancelled."},
                    tasks,
                    published_paths);
            }

            DWORD to_read = static_cast<DWORD>(std::min<uint64_t>(remaining, buffer_size));
            DWORD bytes_read = 0;

            const uint32_t base_fault = context ? (static_cast<uint32_t>(context->extractor_hook.fault) & 0x7F) : 0;

            // Fault injection: short read
            if (base_fault == LPB_EXTRACTOR_FAULT_SHORT_READ &&
                context->extractor_hook.target_artifact == task.target_artifact &&
                bytes_written + to_read >= context->extractor_hook.trigger_after_bytes) {
                CloseHandle(h_temp);
                return rollback_extraction_transaction(
                    context,
                    {LPB_RESULT_INTERNAL_ERROR, "[SourceRangeUnreadable]", "Injected short read for " + std::string(task.artifact_name) + "."},
                    tasks,
                    published_paths);
            }

            if (!ReadFile(task.src_handle, buffer.data(), to_read, &bytes_read, NULL) || bytes_read == 0) {
                CloseHandle(h_temp);
                return rollback_extraction_transaction(
                    context,
                    {LPB_RESULT_INTERNAL_ERROR, "[SourceRangeUnreadable]", "Unexpected EOF or read error while extracting slice for " + std::string(task.artifact_name) + "."},
                    tasks,
                    published_paths);
            }

            // Fault injection: write failure & disk full
            BOOL write_ok = TRUE;
            DWORD bytes_written_chunk = 0;

            if (base_fault != LPB_EXTRACTOR_FAULT_NONE &&
                base_fault != LPB_EXTRACTOR_FAULT_PUBLISH_FAIL &&
                base_fault != LPB_EXTRACTOR_FAULT_FLUSH_DISK_FULL &&
                base_fault != LPB_EXTRACTOR_FAULT_FLUSH_WRITE_FAIL &&
                context->extractor_hook.target_artifact == task.target_artifact &&
                bytes_written + bytes_read >= context->extractor_hook.trigger_after_bytes) {
                if (base_fault == LPB_EXTRACTOR_FAULT_DISK_FULL) {
                    SetLastError(ERROR_DISK_FULL);
                    write_ok = FALSE;
                } else if (base_fault == LPB_EXTRACTOR_FAULT_WRITE_FAIL) {
                    SetLastError(ERROR_WRITE_FAULT);
                    write_ok = FALSE;
                }
            } else {
                write_ok = WriteFile(h_temp, buffer.data(), bytes_read, &bytes_written_chunk, NULL);
            }

            if (!write_ok || bytes_written_chunk != bytes_read) {
                DWORD err = GetLastError();
                CloseHandle(h_temp);
                std::string cat = (err == ERROR_DISK_FULL || err == ERROR_HANDLE_DISK_FULL) ? "[DiskFull]" : "[OutputWriteFailed]";
                std::string det = (cat == "[DiskFull]")
                    ? ("Disk full while writing extracted slice for " + std::string(task.artifact_name) + " (Win32 error: " + std::to_string(err) + ").")
                    : ("Failed while writing extracted slice for " + std::string(task.artifact_name) + " (Win32 error: " + std::to_string(err) + ").");
                return rollback_extraction_transaction(
                    context,
                    {LPB_RESULT_INTERNAL_ERROR, cat, det},
                    tasks,
                    published_paths);
            }

            remaining -= bytes_read;
            bytes_written += bytes_read;

            // Step synchronization callback after writing chunk
            if (context && context->extractor_hook.step_callback &&
                context->extractor_hook.target_artifact == task.target_artifact) {
                context->extractor_hook.step_callback(
                    context->extractor_hook.callback_user_data,
                    task.target_artifact,
                    bytes_written);

                // Immediate cancellation check right after step callback
                if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
                    CloseHandle(h_temp);
                    return rollback_extraction_transaction(
                        context,
                        {LPB_RESULT_CANCELLED, "[Cancelled]", "Source extraction cancelled."},
                        tasks,
                        published_paths);
                }
            }
        }

        // Durability: FlushFileBuffers (BLOCKER C)
        BOOL flush_ok = FlushFileBuffers(h_temp);
        const uint32_t flush_base_fault = context ? (static_cast<uint32_t>(context->extractor_hook.fault) & 0x7F) : 0;
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
            DWORD err = GetLastError();
            CloseHandle(h_temp);
            std::string cat = (err == ERROR_DISK_FULL || err == ERROR_HANDLE_DISK_FULL) ? "[DiskFull]" : "[OutputWriteFailed]";
            std::string det = (cat == "[DiskFull]")
                ? ("Disk full while flushing extracted slice for " + std::string(task.artifact_name) + " (Win32 error: " + std::to_string(err) + ").")
                : ("Failed while flushing extracted slice for " + std::string(task.artifact_name) + " (Win32 error: " + std::to_string(err) + ").");
            return rollback_extraction_transaction(
                context,
                {LPB_RESULT_INTERNAL_ERROR, cat, det},
                tasks,
                published_paths);
        }

        CloseHandle(h_temp);
    }

    // Publication phase: all tasks succeeded staging into temp files.
    // Publish atomically and track published files for complete rollback if any publish step fails.
    for (const auto& task : tasks) {
        if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
            return rollback_extraction_transaction(
                context,
                {LPB_RESULT_CANCELLED, "[Cancelled]", "Source extraction cancelled."},
                tasks,
                published_paths);
        }

        // Test hook for publish failure
        const uint32_t pub_base_fault = context ? (static_cast<uint32_t>(context->extractor_hook.fault) & 0x7F) : 0;
        if (pub_base_fault == LPB_EXTRACTOR_FAULT_PUBLISH_FAIL &&
            context->extractor_hook.target_artifact == task.target_artifact) {
            return rollback_extraction_transaction(
                context,
                {LPB_RESULT_INTERNAL_ERROR, "[OutputPublishFailed]", "Injected publish failure for " + std::string(task.artifact_name) + "."},
                tasks,
                published_paths);
        }

        // Publish using MoveFileExW without MOVEFILE_REPLACE_EXISTING to prevent overwriting existing files
        if (!MoveFileExW(task.temp_path.c_str(), task.final_dst_path.c_str(), MOVEFILE_WRITE_THROUGH)) {
            const DWORD win_err = GetLastError();
            std::string cat = "[OutputPublishFailed]";
            std::string det;
            if (win_err == ERROR_DISK_FULL || win_err == ERROR_HANDLE_DISK_FULL) {
                cat = "[DiskFull]";
                det = "Disk full during publication of " + std::string(task.artifact_name) + " (Win32 error: " + std::to_string(win_err) + ").";
            } else if (win_err == ERROR_FILE_EXISTS || win_err == ERROR_ALREADY_EXISTS) {
                det = "Destination file already exists: " + path_to_utf8(task.final_dst_path) + ".";
            } else {
                det = "Failed to publish extracted slice atomically for " + std::string(task.artifact_name) + " (Win32 error: " + std::to_string(win_err) + ").";
            }
            return rollback_extraction_transaction(
                context,
                {LPB_RESULT_INTERNAL_ERROR, cat, det},
                tasks,
                published_paths);
        }

        published_paths.push_back(task.final_dst_path);
    }

    return LPB_RESULT_OK;
}

} // namespace lpb::media
