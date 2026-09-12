#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include "livephotobox_native.h"
#include "livephotobox_native_version.h"
#include "binary/endian.h"

#include <random>
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

// Creates a unique temporary file with CREATE_NEW so the returned handle is
// provably the FIRST creation of that filesystem object: the kernel atomically
// fails with ERROR_FILE_EXISTS if any other object (including a pre-seeded
// foreign file) already owns the candidate name, and the caller holds the
// creating handle from the very first moment of the object's existence.  This
// is the ownership anchor for the whole Cleaner publish chain: unlike
// GetTempFileNameW (which creates an empty file, closes its handle, and
// returns a pathname that can be raced), no code path ever re-opens a pathname
// to re-establish ownership.  A name collision simply retries with a fresh
// random name.
inline HANDLE lpb_create_unique_temp_file(const std::filesystem::path& dir,
    const wchar_t* prefix, std::filesystem::path& out_path) noexcept
{
    try
    {
        std::random_device rd;
        for (int attempt = 0; attempt < 16; ++attempt)
        {
            const unsigned long r1 = rd();
            const unsigned long r2 = rd();
            std::wstring name = std::wstring(prefix) + L"-"
                + std::to_wstring(static_cast<unsigned long>(GetCurrentProcessId())) + L"-"
                + std::to_wstring(static_cast<unsigned long>(GetTickCount64() & 0xFFFFFFFFull)) + L"-"
                + std::to_wstring(r1) + L"-" + std::to_wstring(r2) + L".tmp";
            const std::filesystem::path candidate = dir / name;
            HANDLE h = CreateFileW(candidate.c_str(), GENERIC_READ | GENERIC_WRITE | DELETE,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (h != INVALID_HANDLE_VALUE)
            {
                out_path = candidate;
                return h;
            }
            const DWORD err = GetLastError();
            if (err != ERROR_FILE_EXISTS && err != ERROR_ALREADY_EXISTS)
            {
                return INVALID_HANDLE_VALUE;
            }
            // Name collision: retry with a fresh random name.
        }
        return INVALID_HANDLE_VALUE;
    }
    catch (...)
    {
        // Any random-device / allocation / conversion failure must surface as
        // a clean failure, never terminate through noexcept.
        return INVALID_HANDLE_VALUE;
    }
}

struct lpb_extractor_test_hook
{
    lpb_extractor_fault fault{LPB_EXTRACTOR_FAULT_NONE};
    int32_t target_artifact{0};
    uint64_t trigger_after_bytes{0};
    lpb_extractor_step_callback step_callback{nullptr};
    void* callback_user_data{nullptr};
};

/* Identity captured before an extraction-owned handle is closed.  The
 * artifact role is descriptive; the Windows file id plus final path is the
 * ownership proof used by rollback and by the later cleanup authority. */
struct lpb_published_artifact_record
{
    int32_t artifact_role{};
    uint32_t auxiliary_index{UINT32_MAX};
    lpb_file_identity identity{};
    std::wstring final_path;
    uint64_t byte_length{};
    std::array<uint8_t, 32> sha256{};
    /* A duplicated transaction-owned handle retained only until commit or
     * rollback.  It lets rollback delete the original object after its path
     * has been replaced, without ever resolving the replacement by name. */
    void* rollback_handle{};
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
    std::vector<lpb_confirmed_residue> confirmed_residues;
    lpb_file_identity primary_identity{};
    lpb_file_identity secondary_identity{};
    bool has_secondary{};
    std::wstring primary_final_path;
    std::wstring secondary_final_path;
    std::vector<lpb_published_artifact_record> published_artifacts;
    bool extraction_succeeded{};
    bool rollback_active{};
    bool rollback_completed{};
    bool committed{};
    bool cleanup_authority_issued{};
    lpb_plan_state state{lpb_plan_state::Issued};
    bool managed_claim_active{};
    bool native_call_active{};
};

/* =========================================================================
 * P3 destructive authority: Native opaque cleanup plan.
 *
 * A cleanup plan is the ONLY way the production Cleaner may request a
 * destructive protocol mutation.  It is issued by a trusted Native chain:
 * either from a P2 extraction plan record (which owns the published artifact
 * identities captured while the transaction handles were open) or, in the
 * test harness only, from caller-supplied facts whose artifact identities are
 * captured at issue time.  Managed DTOs (SourceMediaFacts / cleanup actions /
 * target bindings) never carry destructive authority by themselves.
 *
 * The `artifacts` vector is a detached copy of the P2 published-artifact
 * records: the Windows file id (volume serial + file index) captured from the
 * transaction-owned handle is the ownership proof, `final_path` is
 * descriptive, and byte_length + sha256 are content evidence only.
 * ========================================================================= */
struct lpb_cleanup_plan_record
{
    lpb_context* owner_context{};
    uint32_t abi_version{};
    uint32_t plan_version{};
    uint64_t token{};
    uint64_t generation{};
    lpb_source_media_facts facts{};
    std::vector<lpb_confirmed_residue> confirmed_residues;
    std::vector<lpb_published_artifact_record> artifacts;
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

// Test-only publish race seam configuration.  The FIELD exists in every build
// so the lpb_context layout is identical across the production and harness
// DLLs (a harness build may be handed a production-created context and must
// not write past the production layout).  The seam CODE that reads it is
// compiled only under LPB_NATIVE_TEST_HARNESS, and the setter is a
// harness-only export, so production never reacts to it.
struct lpb_cleaner_test_hook
{
    // One-shot test seam: when non-zero, immediately before publish the owned
    // object is renamed away from its temp pathname (by handle) and a foreign
    // object is created at that pathname.  A handle-based publish must still
    // move the ORIGINAL object to the destination and must leave the foreign
    // object untouched.
    int32_t swap_temp_source_before_publish{0};
};

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
    std::vector<lpb_cleanup_plan_record> cleanup_plans;
    uint64_t next_cleanup_generation{1};
    // Non-zero only while a plan-authorized clean invocation is executing on
    // the thread that entered the cleaner (see cleaner_authority_guard).
    // Combined with the thread-local binding below, this is the unforgeable
    // capability token that raw destructive primitives must present.
    uint64_t active_clean_authority_token{0};
    // Cleaner-owned staged output records captured from the creating handle at
    // publish time (never re-guessed from a pathname after the fact).  Managed
    // code reads these after a clean succeeds OR fails so rollback only ever
    // targets objects this transaction really created.
    std::vector<lpb_published_artifact_record> cleaner_staged_outputs;
    // Always present (layout-stable across production/harness builds); only
    // the harness build reads it.  See lpb_cleaner_test_hook above.
    lpb_cleaner_test_hook cleaner_hook{};
#if defined(LPB_NATIVE_TEST_HARNESS)
    uint64_t test_context_id{};
    uint64_t test_issued{};
    uint64_t test_claimed{};
    uint64_t test_consumed{};
    uint64_t test_released{};
    uint64_t test_cleanup_issued{};
    uint64_t test_cleanup_claimed{};
    uint64_t test_cleanup_consumed{};
    uint64_t test_cleanup_released{};
#endif
};

// Thread-local plan-clean authority binding.  Set only while a
// plan-authorized clean invocation is executing on THIS thread (see
// cleaner_authority_guard in media_cleaner.cpp).  Low-level in-place
// destructive primitives verify that the current thread holds an authority for
// the exact context they are called on, so another thread can never borrow a
// context-wide capability window, and there is no shared mutable counter.
// Thread-local plan-clean authority binding.  Set only while a
// plan-authorized clean invocation is executing on THIS thread (see
// cleaner_authority_guard in media_cleaner.cpp).  The token half is stored in
// the context as well; a raw destructive primitive accepts an invocation only
// when the thread-local binding matches BOTH the context pointer and the
// context's current capability token, so neither a second thread nor a
// same-thread re-entrant callback can borrow the window.
struct lpb_clean_authority_binding
{
    const void* context{nullptr};
    uint64_t token{0};
};
inline thread_local lpb_clean_authority_binding tls_cleanup_authority{};

// True when the current thread is inside the plan-authorized clean invocation
// for the given context (same thread + unforgeable token match).  This is the
// single gate every low-level destructive primitive checks.
inline bool lpb_has_clean_authority(const lpb_context* context) noexcept
{
    return context != nullptr &&
        tls_cleanup_authority.context == static_cast<const void*>(context) &&
        tls_cleanup_authority.token != 0 &&
        tls_cleanup_authority.token == context->active_clean_authority_token;
}

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

/* Cleanup-plan registry helpers.  The public lpb_cleanup_plan* is likewise
 * only an opaque token carrier and is never dereferenced. */
uint64_t cleanup_plan_token_from_handle(const lpb_cleanup_plan* handle) noexcept;
void record_cleaner_staged_output(lpb_context* context, int32_t artifact_role,
    const std::string& path, const lpb_file_identity& identity) noexcept;
// Unified handle-based no-overwrite publication for Cleaner-owned outputs.
//
// The SOURCE object is the one `handle` was created for (CREATE_NEW first
// creation).  It is renamed to `dest` THROUGH THE HANDLE itself
// (SetFileInformationByHandle / FileRenameInfo with ReplaceIfExists = FALSE),
// never through MoveFileExW(sourcePathname, ...): a foreign object that takes
// over the source pathname after creation must not be able to redirect the
// publish.  Identity is captured from the same handle
// (GetFileInformationByHandle) and registered in the transaction's staged
// output registry; the caller then closes the handle.  `temp_source` is the
// pathname the object was created at and is used ONLY by the test-harness
// race seam and for naming; it never re-establishes ownership.
//
// On any failure the object is left in place (either still at `temp_source`,
// or, after a successful rename, at `dest`), and the caller must dispose of
// it through this same handle (FileDispositionInfo).  This function never
// deletes by pathname and never touches a foreign object.  A destination that
// already exists (including a foreign object placed there mid-flight) fails
// closed and the owned object is deleted through the handle.
inline bool lpb_publish_cleaner_output_handle(
    lpb_context* context,
    int32_t artifact_role,
    HANDLE handle,
    const std::filesystem::path& temp_source,
    const std::filesystem::path& dest,
    const std::string& dest_path)
{
    // temp_source is consumed only by the test-harness race seam below; in
    // production builds it is intentionally unused (never re-establishes
    // ownership) and is referenced purely to keep the parameter meaningful.
    static_cast<void>(temp_source);
#if defined(LPB_NATIVE_TEST_HARNESS)
    if (context != nullptr && context->cleaner_hook.swap_temp_source_before_publish != 0)
    {
        context->cleaner_hook.swap_temp_source_before_publish = 0;
        try
        {
            // Stage the race: move the owned object (by THIS handle) to a side
            // pathname, then create a foreign object at the original temp
            // pathname.  The publish below must still move the ORIGINAL object
            // and must leave the foreign object untouched.
            std::filesystem::path side = temp_source;
            side += L".lpb-race-side";
            const std::wstring side_w = side.native();
            std::vector<uint8_t> side_buf(sizeof(FILE_RENAME_INFO) + side_w.size() * sizeof(wchar_t));
            auto* side_info = reinterpret_cast<FILE_RENAME_INFO*>(side_buf.data());
            std::memset(side_info, 0, side_buf.size());
            side_info->ReplaceIfExists = FALSE;
            side_info->RootDirectory = nullptr;
            side_info->FileNameLength = static_cast<DWORD>(side_w.size() * sizeof(wchar_t));
            std::memcpy(side_info->FileName, side_w.c_str(), side_info->FileNameLength);
            if (!SetFileInformationByHandle(handle, FileRenameInfo, side_info, static_cast<DWORD>(side_buf.size())))
            {
                // Could not stage the takeover; fail closed rather than run a
                // publish the test cannot distinguish from a benign path.
                return false;
            }
            HANDLE foreign = CreateFileW(temp_source.c_str(), GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (foreign == INVALID_HANDLE_VALUE)
            {
                return false;
            }
            const char marker[] = "LPB-FOREIGN-RACE-MARKER";
            DWORD written = 0;
            static_cast<void>(WriteFile(foreign, marker, static_cast<DWORD>(sizeof(marker) - 1), &written, nullptr));
            CloseHandle(foreign);
        }
        catch (...)
        {
            return false;
        }
    }
#endif
    try
    {
        const std::wstring dest_w = dest.native();
        std::vector<uint8_t> buf(sizeof(FILE_RENAME_INFO) + dest_w.size() * sizeof(wchar_t));
        auto* rename_info = reinterpret_cast<FILE_RENAME_INFO*>(buf.data());
        std::memset(rename_info, 0, buf.size());
        rename_info->ReplaceIfExists = FALSE;
        rename_info->RootDirectory = nullptr;
        rename_info->FileNameLength = static_cast<DWORD>(dest_w.size() * sizeof(wchar_t));
        std::memcpy(rename_info->FileName, dest_w.c_str(), rename_info->FileNameLength);
        if (!SetFileInformationByHandle(handle, FileRenameInfo, rename_info, static_cast<DWORD>(buf.size())))
        {
            return false;
        }
        BY_HANDLE_FILE_INFORMATION finfo{};
        if (!GetFileInformationByHandle(handle, &finfo))
        {
            return false;
        }
        lpb_file_identity identity{};
        identity.volume_serial = finfo.dwVolumeSerialNumber;
        identity.file_index = (static_cast<uint64_t>(finfo.nFileIndexHigh) << 32) | finfo.nFileIndexLow;
        identity.file_size = (static_cast<uint64_t>(finfo.nFileSizeHigh) << 32) | finfo.nFileSizeLow;
        identity.link_count = finfo.nNumberOfLinks;
        record_cleaner_staged_output(context, artifact_role, dest_path, identity);
        return true;
    }
    catch (...)
    {
        return false;
    }
}
// Removes a staged output ONLY if the filesystem object currently at the path
// still matches the identity this transaction registered (created-handle
// identity).  Never a bare pathname delete: a same-content replacement or any
// foreign object that took over the path is left untouched (fail closed).
size_t get_cleaner_staged_outputs(lpb_context* context,
    lpb_clean_staged_output_record* out_records, size_t capacity) noexcept;
lpb_cleanup_plan* cleanup_plan_handle_from_token(uint64_t token) noexcept;
lpb_cleanup_plan_record* find_cleanup_plan_locked(lpb_context* context, uint64_t token) noexcept;
bool generate_cleanup_plan_token(lpb_context* context, uint64_t& token) noexcept;
// Token generation that assumes the caller already holds context->plan_mutex.
// lpb_issue_cleanup_plan uses it to avoid a self-deadlock on the non-recursive
// plan_mutex (lock -> generate -> lock again).
bool generate_cleanup_plan_token_unlocked(lpb_context* context, uint64_t& token) noexcept;

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

/* Closes transaction-owned output handles without touching the files.  This
 * is used by release/context teardown safety paths when commit or rollback
 * was not reached. */
void close_published_artifact_handles(
    lpb_extraction_plan_record& record) noexcept;

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
bool test_get_cleanup_plan_accounting(
    lpb_context* context,
    lpb_test_plan_accounting& accounting) noexcept;
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
