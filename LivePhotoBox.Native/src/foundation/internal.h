#pragma once
#include "livephotobox_native.h"
#include "livephotobox_native_version.h"
#include "binary/endian.h"

#include <cstring>
#include <algorithm>
#include <array>
#include <cstdint>
#include <limits>
#include <mutex>
#include <new>
#include <string>
#include <utility>
#include <vector>

#ifndef LPB_PRODUCT_VERSION
#define LPB_PRODUCT_VERSION "0.0.0.0"
#endif

#include <filesystem>

struct lpb_extractor_test_hook
{
    lpb_extractor_fault fault{LPB_EXTRACTOR_FAULT_NONE};
    int32_t target_artifact{0};
    uint64_t trigger_after_bytes{0};
    lpb_extractor_step_callback step_callback{nullptr};
    void* callback_user_data{nullptr};
};

struct lpb_context
{
    lpb_log_callback log_callback{};
    lpb_cancel_callback cancel_callback{};
    void* user_data{};
    std::mutex error_mutex;
    std::string last_error;
    lpb_extractor_test_hook extractor_hook{};
};

constexpr size_t context_options_v1_size =
    offsetof(lpb_context_options, user_data) + sizeof(lpb_context_options::user_data);

void set_error(lpb_context* context, const char* message) noexcept;
void log_message(lpb_context* context, lpb_log_level level, const char* message) noexcept;
std::filesystem::path utf8_to_path(const char* utf8_str) noexcept;
std::string path_to_utf8(const std::filesystem::path& path) noexcept;
bool paths_alias(const char* first, const char* second) noexcept;
lpb_result copy_output(
    lpb_context* context,
    const std::vector<uint8_t>& value,
    uint8_t* output,
    size_t output_size,
    size_t* required_size) noexcept;
