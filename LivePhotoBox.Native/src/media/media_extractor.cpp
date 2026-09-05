#include "media/media_extractor.h"
#include "foundation/internal.h"
#include <fstream>
#include <filesystem>
#include <Windows.h>
#include <vector>
#include <string>
#include <algorithm>
#include <cstdint>

namespace lpb::media {

namespace {

struct slice_task {
    const char* src_path{nullptr};
    uint64_t offset{0};
    uint64_t length{0};
    const char* dst_path{nullptr};
    std::filesystem::path final_dst_path;
    std::filesystem::path temp_path;
    int32_t target_artifact{0}; // 0 = PrimaryImage, 1 = MotionVideo, 2 = GainMap
    const char* artifact_name{"Unknown"};
};

static void cleanup_task_temp_files(const std::vector<slice_task>& tasks) noexcept {
    std::error_code ec;
    for (const auto& task : tasks) {
        if (!task.temp_path.empty()) {
            std::filesystem::remove(task.temp_path, ec);
        }
    }
}

static void rollback_published_files(const std::vector<std::filesystem::path>& published_paths) noexcept {
    std::error_code ec;
    for (const auto& path : published_paths) {
        std::filesystem::remove(path, ec);
    }
}

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

    // Defensive validation of input facts
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

    if (facts->motion_video.is_present) {
        if (facts->motion_video.file_range.length == 0) {
            set_error(context, "[InvalidFacts] Motion video range length must be greater than zero when video is present.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (facts->motion_video.container == LPB_VIDEO_CONTAINER_UNKNOWN) {
            set_error(context, "[UnsupportedLayout] Motion video container is unknown or unsupported.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (facts->motion_video.source_index < 0 || facts->motion_video.source_index > 1) {
            set_error(context, "[InvalidFacts] Invalid motion video source index in facts.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (facts->motion_video.source_index == 1 && (!secondary_path || secondary_path[0] == '\0')) {
            set_error(context, "[InvalidFacts] Motion video requires secondary source file, but none was provided.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
    }

    if (facts->gain_map.is_present) {
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

    // Plan tasks
    std::vector<slice_task> tasks;
    tasks.reserve(3);

    // 1. Primary Image
    if (output_image_path && output_image_path[0] != '\0') {
        slice_task t{};
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
        const char* vid_src = (facts->motion_video.source_index == 1) ? secondary_path : primary_path;
        slice_task t{};
        t.src_path = vid_src;
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

    // Stage all tasks into temporary files
    for (auto& task : tasks) {
        if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
            cleanup_task_temp_files(tasks);
            set_error(context, "[Cancelled] Source extraction cancelled.");
            return LPB_RESULT_CANCELLED;
        }

        auto p_src = utf8_to_path(task.src_path);
        std::error_code ec;
        const uintmax_t source_size = std::filesystem::file_size(p_src, ec);
        if (ec || task.offset > source_size || task.length > source_size - task.offset) {
            cleanup_task_temp_files(tasks);
            std::string msg = "[SourceRangeUnreadable] Requested " + std::string(task.artifact_name) +
                " range is outside the source file boundary.";
            set_error(context, msg.c_str());
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        std::ifstream in(p_src, std::ios::binary);
        if (!in.is_open()) {
            cleanup_task_temp_files(tasks);
            std::string msg = "[SourceRangeUnreadable] Failed to open source file for reading " +
                std::string(task.artifact_name) + ".";
            set_error(context, msg.c_str());
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        in.seekg(task.offset, std::ios::beg);
        if (!in.good()) {
            cleanup_task_temp_files(tasks);
            std::string msg = "[SourceRangeUnreadable] Failed to seek to slice offset in source file for " +
                std::string(task.artifact_name) + ".";
            set_error(context, msg.c_str());
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        auto p_dst = utf8_to_path(task.dst_path);
        task.final_dst_path = p_dst;

        ec.clear();
        if (std::filesystem::exists(p_dst, ec) && std::filesystem::is_directory(p_dst, ec)) {
            cleanup_task_temp_files(tasks);
            std::string msg = "[OutputWriteFailed] Destination path is a directory for " +
                std::string(task.artifact_name) + ".";
            set_error(context, msg.c_str());
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        auto temp_dir = p_dst.parent_path();
        ec.clear();
        if (temp_dir.empty()) {
            temp_dir = std::filesystem::current_path(ec);
        }
        if (ec || !std::filesystem::exists(temp_dir, ec) || !std::filesystem::is_directory(temp_dir, ec)) {
            cleanup_task_temp_files(tasks);
            std::string msg = "[OutputPublishFailed] Destination directory does not exist or is invalid for " +
                std::string(task.artifact_name) + ".";
            set_error(context, msg.c_str());
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        wchar_t temp_name[MAX_PATH]{};
        if (GetTempFileNameW(temp_dir.c_str(), L"lpb", 0, temp_name) == 0) {
            cleanup_task_temp_files(tasks);
            std::string msg = "[OutputPublishFailed] Failed to allocate temporary extraction file for " +
                std::string(task.artifact_name) + ".";
            set_error(context, msg.c_str());
            return LPB_RESULT_INTERNAL_ERROR;
        }
        task.temp_path = temp_name;

        std::ofstream out(task.temp_path, std::ios::binary | std::ios::trunc);
        if (!out.is_open()) {
            cleanup_task_temp_files(tasks);
            std::string msg = "[OutputWriteFailed] Failed to open temporary file for writing " +
                std::string(task.artifact_name) + ".";
            set_error(context, msg.c_str());
            return LPB_RESULT_INTERNAL_ERROR;
        }

        constexpr size_t buffer_size = 64 * 1024;
        std::vector<char> buffer(buffer_size);
        uint64_t remaining = task.length;
        uint64_t bytes_written = 0;

        while (remaining > 0) {
            if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
                out.close();
                cleanup_task_temp_files(tasks);
                set_error(context, "[Cancelled] Source extraction cancelled.");
                return LPB_RESULT_CANCELLED;
            }

            // Fault injection & step synchronization
            if (context) {
                if (context->extractor_hook.step_callback &&
                    context->extractor_hook.target_artifact == task.target_artifact) {
                    context->extractor_hook.step_callback(
                        context->extractor_hook.callback_user_data,
                        0,
                        bytes_written);
                }

                if (context->extractor_hook.fault != LPB_EXTRACTOR_FAULT_NONE &&
                    context->extractor_hook.fault != LPB_EXTRACTOR_FAULT_PUBLISH_FAIL &&
                    context->extractor_hook.target_artifact == task.target_artifact &&
                    bytes_written >= context->extractor_hook.trigger_after_bytes) {
                    out.close();
                    cleanup_task_temp_files(tasks);
                    if (context->extractor_hook.fault == LPB_EXTRACTOR_FAULT_DISK_FULL) {
                        std::string msg = "[DiskFull] Disk full while writing extracted slice for " +
                            std::string(task.artifact_name) + ".";
                        set_error(context, msg.c_str());
                        return LPB_RESULT_INTERNAL_ERROR;
                    } else if (context->extractor_hook.fault == LPB_EXTRACTOR_FAULT_WRITE_FAIL) {
                        std::string msg = "[OutputWriteFailed] Injected write failure for " +
                            std::string(task.artifact_name) + ".";
                        set_error(context, msg.c_str());
                        return LPB_RESULT_INTERNAL_ERROR;
                    } else if (context->extractor_hook.fault == LPB_EXTRACTOR_FAULT_SHORT_READ) {
                        std::string msg = "[SourceRangeUnreadable] Injected short read for " +
                            std::string(task.artifact_name) + ".";
                        set_error(context, msg.c_str());
                        return LPB_RESULT_INTERNAL_ERROR;
                    }
                }
            }

            size_t to_read = static_cast<size_t>(std::min<uint64_t>(remaining, buffer_size));
            in.read(buffer.data(), to_read);
            std::streamsize read_bytes = in.gcount();
            if (read_bytes <= 0 || (read_bytes < static_cast<std::streamsize>(to_read) && remaining > static_cast<uint64_t>(read_bytes))) {
                out.close();
                cleanup_task_temp_files(tasks);
                std::string msg = "[SourceRangeUnreadable] Unexpected EOF while extracting slice for " +
                    std::string(task.artifact_name) + ".";
                set_error(context, msg.c_str());
                return LPB_RESULT_INTERNAL_ERROR;
            }

            out.write(buffer.data(), read_bytes);
            if (!out.good()) {
                out.close();
                cleanup_task_temp_files(tasks);
                std::string msg = "[OutputWriteFailed] Failed while writing extracted slice for " +
                    std::string(task.artifact_name) + ".";
                set_error(context, msg.c_str());
                return LPB_RESULT_INTERNAL_ERROR;
            }

            remaining -= read_bytes;
            bytes_written += read_bytes;
        }

        out.flush();
        if (!out.good()) {
            out.close();
            cleanup_task_temp_files(tasks);
            std::string msg = "[OutputWriteFailed] Failed to flush extracted slice for " +
                std::string(task.artifact_name) + ".";
            set_error(context, msg.c_str());
            return LPB_RESULT_INTERNAL_ERROR;
        }
        out.close();
    }

    // Publication phase: all tasks succeeded staging into temp files.
    // Publish atomically and track published files for complete rollback if any publish step fails.
    std::vector<std::filesystem::path> published_paths;
    published_paths.reserve(tasks.size());

    for (const auto& task : tasks) {
        if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
            cleanup_task_temp_files(tasks);
            rollback_published_files(published_paths);
            set_error(context, "[Cancelled] Source extraction cancelled.");
            return LPB_RESULT_CANCELLED;
        }

        // Test hook for publish failure
        if (context &&
            context->extractor_hook.fault == LPB_EXTRACTOR_FAULT_PUBLISH_FAIL &&
            context->extractor_hook.target_artifact == task.target_artifact) {
            cleanup_task_temp_files(tasks);
            rollback_published_files(published_paths);
            std::string msg = "[OutputPublishFailed] Injected publish failure for " +
                std::string(task.artifact_name) + ".";
            set_error(context, msg.c_str());
            return LPB_RESULT_INTERNAL_ERROR;
        }

        if (!MoveFileExW(task.temp_path.c_str(), task.final_dst_path.c_str(),
                         MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
            const DWORD win_err = GetLastError();
            cleanup_task_temp_files(tasks);
            rollback_published_files(published_paths);
            std::string msg = "[OutputPublishFailed] Failed to publish extracted slice atomically for " +
                std::string(task.artifact_name) + " (Win32 error: " + std::to_string(win_err) + ").";
            set_error(context, msg.c_str());
            return LPB_RESULT_INTERNAL_ERROR;
        }

        published_paths.push_back(task.final_dst_path);
    }

    return LPB_RESULT_OK;
}

} // namespace lpb::media
