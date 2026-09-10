#pragma once
#include "livephotobox_native.h"
#include "livephotobox_native_version.h"
#include "binary/endian.h"

#include <cstring>
#include <algorithm>
#include <array>
#include <atomic>
#include <condition_variable>
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

struct lpb_file_identity
{
    uint32_t volume_serial{};
    uint64_t file_index{};
    uint64_t file_size{};
    uint32_t link_count{};
};

/*
 * The public lpb_extraction_plan* is only an opaque token carrier.  It is
 * never dereferenced and is deliberately not the address of this record.
 * All extraction authority stays in this context-owned registry value.
 */
enum class lpb_plan_state : uint8_t
{
    Issued,
    Claimed,
    Consumed,
    ReleaseRequested,
    Released
};

struct lpb_extraction_plan_record
{
    lpb_context* owner_context{};
    uint32_t abi_version{};
    uint32_t plan_version{};
    uint64_t token{};
    uint64_t generation{};
    lpb_source_media_facts facts{};
    lpb_file_identity primary_identity{};
    lpb_file_identity secondary_identity{};
    bool has_secondary{};
    std::wstring primary_final_path;
    std::wstring secondary_final_path;
    lpb_plan_state state{lpb_plan_state::Issued};
    bool managed_claim_active{};
    bool native_call_active{};
};

#if defined(LPB_NATIVE_TEST_HARNESS)
struct lpb_test_destroyed_context_archive
{
    lpb_test_plan_accounting accounting{};
    std::vector<uint64_t> plan_tokens;
};
#endif

struct lpb_context
{
    lpb_log_callback log_callback{};
    lpb_cancel_callback cancel_callback{};
    void* user_data{};
    std::mutex error_mutex;
    std::string last_error;
    lpb_inspection_status inspection_status{};
    lpb_extractor_test_hook extractor_hook{};
    lpb_cleaner_snapshot_callback cleaner_post_snapshot_callback{nullptr};
    void* cleaner_callback_user_data{nullptr};
    std::mutex lifetime_mutex;
    std::condition_variable lifetime_cv;
    uint32_t active_operations{};
    bool destroy_requested{};
    std::mutex plan_mutex;
    std::vector<lpb_extraction_plan_record> extraction_plans;
    uint64_t next_plan_generation{1};
#if defined(LPB_NATIVE_TEST_HARNESS)
    uint64_t test_context_id{};
    uint64_t test_issued{};
    uint64_t test_claimed{};
    uint64_t test_consumed{};
    uint64_t test_released{};
#endif
};

class lpb_context_operation
{
public:
    explicit lpb_context_operation(lpb_context* context) noexcept;
    lpb_context_operation(const lpb_context_operation&) = delete;
    lpb_context_operation& operator=(const lpb_context_operation&) = delete;
    ~lpb_context_operation() noexcept;

    bool acquired() const noexcept { return context_ != nullptr; }

private:
    lpb_context* context_{};
};

uint64_t plan_token_from_handle(const lpb_extraction_plan* handle) noexcept;
lpb_extraction_plan* plan_handle_from_token(uint64_t token) noexcept;
lpb_extraction_plan_record* find_plan_locked(lpb_context* context, uint64_t token) noexcept;
bool generate_plan_token(lpb_context* context, uint64_t& token) noexcept;

struct lpb_plan_snapshot
{
    lpb_source_media_facts facts{};
    lpb_file_identity primary_identity{};
    lpb_file_identity secondary_identity{};
    bool has_secondary{};
    std::wstring primary_final_path;
    std::wstring secondary_final_path;
};

/* Atomically claims the native call portion of an already managed-claimed or
 * directly issued plan and copies the complete authority snapshot. */
lpb_result begin_plan_native_call(
    lpb_context* context,
    const lpb_extraction_plan* handle,
    lpb_plan_snapshot& snapshot) noexcept;

/* Finishes a native or managed attempt and leaves a tombstone for replay
 * detection.  It is safe to call from every failure path. */
void finish_plan_attempt(
    lpb_context* context,
    uint64_t token) noexcept;

#if defined(LPB_NATIVE_TEST_HARNESS)
uint64_t test_context_id(const lpb_context* context) noexcept;
bool test_get_plan_accounting(
    lpb_context* context,
    lpb_test_plan_accounting& accounting) noexcept;
bool test_get_destroyed_plan_accounting(
    uint64_t context_id,
    lpb_test_plan_accounting& accounting) noexcept;
lpb_result test_probe_destroyed_plan(
    uint64_t context_id,
    uint64_t plan_token) noexcept;
void test_archive_destroyed_context(lpb_context* context) noexcept;
#endif

constexpr size_t context_options_v1_size =
    offsetof(lpb_context_options, user_data) + sizeof(lpb_context_options::user_data);

void set_error(lpb_context* context, const char* message) noexcept;
void set_inspection_status(lpb_context* context, lpb_inspection_failure_category category,
    lpb_inspection_stage stage, uint64_t capability = 0) noexcept;
void log_message(lpb_context* context, lpb_log_level level, const char* message) noexcept;
std::filesystem::path utf8_to_path(const char* utf8_str) noexcept;
std::string path_to_utf8(const std::filesystem::path& path) noexcept;
bool paths_alias(const char* first, const char* second) noexcept;
bool capture_file_identity(
    const char* utf8_path,
    lpb_file_identity& identity,
    std::wstring& final_path) noexcept;
bool capture_file_identity_from_handle(
    void* file_handle,
    lpb_file_identity& identity,
    std::wstring& final_path) noexcept;
lpb_result copy_output(
    lpb_context* context,
    const std::vector<uint8_t>& value,
    uint8_t* output,
    size_t output_size,
    size_t* required_size) noexcept;
