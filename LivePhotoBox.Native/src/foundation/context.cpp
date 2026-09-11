#include "foundation/internal.h"
#include <windows.h>
#include <bcrypt.h>

#pragma comment(lib, "bcrypt.lib")

#if defined(LPB_NATIVE_TEST_HARNESS)
namespace
{
std::atomic<uint64_t> g_next_test_context_id{1};
std::mutex g_destroyed_context_archive_mutex;
std::vector<lpb_test_destroyed_context_archive> g_destroyed_context_archives;

bool is_live_plan_state(lpb_plan_state state) noexcept
{
    return state == lpb_plan_state::Issued ||
        state == lpb_plan_state::Claimed ||
        state == lpb_plan_state::ReleaseRequested;
}
}
#endif

lpb_context_operation::lpb_context_operation(lpb_context* context) noexcept
{
    if (context == nullptr)
    {
        return;
    }

    try
    {
        std::unique_lock lock(context->lifetime_mutex);
        if (context->destroy_requested)
        {
            return;
        }
        ++context->active_operations;
        context_ = context;
    }
    catch (...)
    {
        context_ = nullptr;
    }
}

lpb_context_operation::~lpb_context_operation() noexcept
{
    if (context_ == nullptr)
    {
        return;
    }

    try
    {
        std::unique_lock lock(context_->lifetime_mutex);
        if (context_->active_operations > 0)
        {
            --context_->active_operations;
        }
        if (context_->destroy_requested && context_->active_operations == 0)
        {
            context_->lifetime_cv.notify_all();
        }
    }
    catch (...)
    {
        // A lifetime lease must never throw through a C ABI boundary.
    }
}

uint64_t plan_token_from_handle(const lpb_extraction_plan* handle) noexcept
{
    // This conversion is intentionally the only operation performed on a
    // caller-provided plan handle.  The pointed-to type is never dereferenced.
    return static_cast<uint64_t>(reinterpret_cast<uintptr_t>(handle));
}

lpb_extraction_plan* plan_handle_from_token(uint64_t token) noexcept
{
    return reinterpret_cast<lpb_extraction_plan*>(static_cast<uintptr_t>(token));
}

lpb_extraction_plan_record* find_plan_locked(lpb_context* context, uint64_t token) noexcept
{
    if (context == nullptr || token == 0)
    {
        return nullptr;
    }

    for (auto& record : context->extraction_plans)
    {
        if (record.token == token)
        {
            return &record;
        }
    }
    return nullptr;
}

bool generate_plan_token(lpb_context* context, uint64_t& token) noexcept
{
    token = 0;
    if (context == nullptr)
    {
        return false;
    }

    for (int attempt = 0; attempt < 32; ++attempt)
    {
        uint64_t candidate = 0;
        if (BCryptGenRandom(
                nullptr,
                reinterpret_cast<PUCHAR>(&candidate),
                static_cast<ULONG>(sizeof(candidate)),
                BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0 || candidate == 0)
        {
            continue;
        }

        std::scoped_lock lock(context->plan_mutex);
        if (find_plan_locked(context, candidate) == nullptr)
        {
            token = candidate;
            return true;
        }
    }
    return false;
}

lpb_result begin_plan_native_call(
    lpb_context* context,
    const lpb_extraction_plan* handle,
    lpb_plan_snapshot& snapshot) noexcept
{
    if (context == nullptr || handle == nullptr)
    {
        set_error(context, "[AuthorityViolation] Context and extraction plan are required.");
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }

    const uint64_t token = plan_token_from_handle(handle);
    try
    {
        std::scoped_lock lock(context->plan_mutex);
        lpb_extraction_plan_record* record = find_plan_locked(context, token);
        if (record == nullptr || record->owner_context != context)
        {
            set_error(context, "[AuthorityViolation] Extraction plan token is not owned by this Native context.");
            return LPB_RESULT_AUTHORITY_VIOLATION;
        }
        if (record->abi_version != LPB_NATIVE_ABI_VERSION ||
            record->plan_version != 1 ||
            record->generation == 0 ||
            record->facts.struct_size < sizeof(lpb_source_media_facts))
        {
            set_error(context, "[AuthorityViolation] Extraction plan metadata is invalid.");
            return LPB_RESULT_AUTHORITY_VIOLATION;
        }

        if (record->state == lpb_plan_state::Issued)
        {
            record->state = lpb_plan_state::Claimed;
#if defined(LPB_NATIVE_TEST_HARNESS)
            ++context->test_claimed;
#endif
        }
        else if (record->state != lpb_plan_state::Claimed)
        {
            set_error(context, "[PlanReplayed] Extraction plan is released or has already been consumed.");
            return LPB_RESULT_PLAN_REPLAYED;
        }

        if (record->native_call_active)
        {
            set_error(context, "[PlanReplayed] Extraction plan is already in use by another native operation.");
            return LPB_RESULT_PLAN_REPLAYED;
        }

        record->native_call_active = true;
        try
        {
            snapshot.facts = record->facts;
            snapshot.primary_identity = record->primary_identity;
            snapshot.secondary_identity = record->secondary_identity;
            snapshot.has_secondary = record->has_secondary;
            snapshot.primary_final_path = record->primary_final_path;
            snapshot.secondary_final_path = record->secondary_final_path;
        }
        catch (...)
        {
            record->native_call_active = false;
            record->state = lpb_plan_state::Consumed;
            set_error(context, "[InternalError] Failed to copy the extraction authority record.");
            return LPB_RESULT_INTERNAL_ERROR;
        }
        return LPB_RESULT_OK;
    }
    catch (...)
    {
        set_error(context, "[InternalError] Failed to access the extraction authority registry.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
}

void close_published_artifact_handles(lpb_extraction_plan_record& record) noexcept
{
    for (auto& artifact : record.published_artifacts)
    {
        if (artifact.rollback_handle != nullptr && artifact.rollback_handle != INVALID_HANDLE_VALUE)
        {
            (void)CloseHandle(static_cast<HANDLE>(artifact.rollback_handle));
            artifact.rollback_handle = nullptr;
        }
    }
}

void finish_plan_attempt(lpb_context* context, uint64_t token) noexcept
{
    if (context == nullptr || token == 0)
    {
        return;
    }

    try
    {
        std::scoped_lock lock(context->plan_mutex);
        lpb_extraction_plan_record* record = find_plan_locked(context, token);
        if (record == nullptr || record->owner_context != context)
        {
            return;
        }

        record->native_call_active = false;
        // A managed caller may explicitly claim the plan before invoking the
        // extractor so it can verify or exactly roll back the published
        // objects before FinishExtractionPlan consumes the authority.  Do not
        // consume that claim merely because this one native call has returned.
        // The legacy/direct native path never sets managed_claim_active and is
        // still consumed here as before.
        if (record->state == lpb_plan_state::Claimed && !record->managed_claim_active)
        {
            record->state = lpb_plan_state::Consumed;
#if defined(LPB_NATIVE_TEST_HARNESS)
            ++context->test_consumed;
#endif
        }
        else if (record->state == lpb_plan_state::ReleaseRequested)
        {
            close_published_artifact_handles(*record);
            record->managed_claim_active = false;
            record->state = lpb_plan_state::Released;
#if defined(LPB_NATIVE_TEST_HARNESS)
            ++context->test_released;
#endif
        }
    }
    catch (...)
    {
        // The tombstone is best-effort here; never throw from cleanup.
    }
}

void set_error(lpb_context* context, const char* message) noexcept
{
    if (context == nullptr)
    {
        return;
    }

    try
    {
        std::scoped_lock lock(context->error_mutex);
        context->last_error = message == nullptr ? "Unknown native error." : message;
    }
    catch (...)
    {
        // Error reporting must never throw through the C ABI.
    }
}

void set_inspection_status(lpb_context* context, lpb_inspection_failure_category category,
    lpb_inspection_stage stage, uint64_t capability) noexcept
{
    if (context == nullptr) return;
    try {
        std::scoped_lock lock(context->error_mutex);
        context->inspection_status.struct_size = sizeof(lpb_inspection_status);
        context->inspection_status.category = category;
        context->inspection_status.stage = stage;
        context->inspection_status.capability = capability;
    } catch (...) { }
}

void log_message(lpb_context* context, lpb_log_level level, const char* message) noexcept
{
    if (context == nullptr || context->log_callback == nullptr || message == nullptr)
    {
        return;
    }

    try
    {
        context->log_callback(context->user_data, level, message, std::strlen(message));
    }
    catch (...)
    {
        set_error(context, "A native log callback failed.");
    }
}

std::filesystem::path utf8_to_path(const char* utf8_str) noexcept
{
    if (utf8_str == nullptr || *utf8_str == '\0')
    {
        return {};
    }

    int wlen = MultiByteToWideChar(CP_UTF8, 0, utf8_str, -1, nullptr, 0);
    if (wlen <= 1)
    {
        return {};
    }

    std::wstring wstr(wlen, 0);
    MultiByteToWideChar(CP_UTF8, 0, utf8_str, -1, wstr.data(), wlen);
    if (!wstr.empty() && wstr.back() == L'\0')
    {
        wstr.pop_back();
    }

    return std::filesystem::path(wstr);
}

std::string path_to_utf8(const std::filesystem::path& path) noexcept
{
    const std::wstring& wstr = path.wstring();
    if (wstr.empty()) return {};
    int ulen = WideCharToMultiByte(CP_UTF8, 0, wstr.data(), static_cast<int>(wstr.size()), nullptr, 0, nullptr, nullptr);
    if (ulen <= 0) return {};
    std::string ustr(ulen, 0);
    WideCharToMultiByte(CP_UTF8, 0, wstr.data(), static_cast<int>(wstr.size()), ustr.data(), ulen, nullptr, nullptr);
    return ustr;
}

bool capture_file_identity_from_handle(
    void* file_handle,
    lpb_file_identity& identity,
    std::wstring& final_path) noexcept
{
    identity = {};
    final_path.clear();
    const HANDLE handle = static_cast<HANDLE>(file_handle);
    if (handle == nullptr || handle == INVALID_HANDLE_VALUE)
    {
        return false;
    }

    BY_HANDLE_FILE_INFORMATION info{};
    if (!GetFileInformationByHandle(handle, &info))
    {
        return false;
    }

    identity.volume_serial = info.dwVolumeSerialNumber;
    identity.file_index = (static_cast<uint64_t>(info.nFileIndexHigh) << 32) |
        static_cast<uint64_t>(info.nFileIndexLow);
    identity.file_size = (static_cast<uint64_t>(info.nFileSizeHigh) << 32) |
        static_cast<uint64_t>(info.nFileSizeLow);
    identity.link_count = info.nNumberOfLinks;

    std::vector<wchar_t> buffer(512);
    for (;;)
    {
        const DWORD length = GetFinalPathNameByHandleW(
            handle, buffer.data(), static_cast<DWORD>(buffer.size()), FILE_NAME_NORMALIZED);
        if (length == 0)
        {
            return false;
        }
        if (length < buffer.size())
        {
            final_path.assign(buffer.data(), length);
            return true;
        }
        if (buffer.size() >= 32768)
        {
            return false;
        }
        buffer.resize(buffer.size() * 2);
    }
}

bool capture_file_identity(
    const char* utf8_path,
    lpb_file_identity& identity,
    std::wstring& final_path) noexcept
{
    identity = {};
    final_path.clear();
    const auto path = utf8_to_path(utf8_path);
    if (path.empty())
    {
        return false;
    }

    const HANDLE handle = CreateFileW(
        path.c_str(),
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_BACKUP_SEMANTICS,
        nullptr);
    if (handle == INVALID_HANDLE_VALUE)
    {
        return false;
    }

    const bool result = capture_file_identity_from_handle(handle, identity, final_path);
    CloseHandle(handle);
    return result;
}

bool paths_alias(const char* first, const char* second) noexcept
{
    try
    {
        const auto left = utf8_to_path(first);
        const auto right = utf8_to_path(second);
        if (left.empty() || right.empty()) return false;

        std::error_code left_ec;
        std::error_code right_ec;
        auto left_absolute = std::filesystem::absolute(left, left_ec);
        auto right_absolute = std::filesystem::absolute(right, right_ec);
        if (left_ec) left_absolute = left;
        if (right_ec) right_absolute = right;
        std::wstring left_norm = left_absolute.lexically_normal().wstring();
        std::wstring right_norm = right_absolute.lexically_normal().wstring();
        if (_wcsicmp(left_norm.c_str(), right_norm.c_str()) == 0) return true;

        std::error_code equivalent_ec;
        return std::filesystem::equivalent(left, right, equivalent_ec) && !equivalent_ec;
    }
    catch (...)
    {
        return true; // Fail closed: cannot prove paths are distinct
    }
}

lpb_result copy_output(
    lpb_context* context,
    const std::vector<uint8_t>& value,
    uint8_t* output,
    size_t output_size,
    size_t* required_size) noexcept
{
    if (required_size == nullptr)
    {
        set_error(context, "A required-size pointer is required.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    *required_size = value.size();
    if (output == nullptr || output_size < value.size())
    {
        return LPB_RESULT_BUFFER_TOO_SMALL;
    }
    if (!value.empty())
    {
        std::memcpy(output, value.data(), value.size());
    }
    return LPB_RESULT_OK;
}

uint32_t LPB_CALL lpb_get_abi_version(void)
{
    return LPB_NATIVE_ABI_VERSION;
}

const char* LPB_CALL lpb_get_version(void)
{
    return LPB_PRODUCT_VERSION;
}

lpb_result LPB_CALL lpb_create_context(
    const lpb_context_options* options,
    lpb_context** context)
{
    if (context == nullptr)
    {
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    *context = nullptr;

    if (options != nullptr)
    {
        if (options->struct_size < context_options_v1_size)
        {
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        if (options->abi_version != LPB_NATIVE_ABI_VERSION)
        {
            return LPB_RESULT_ABI_MISMATCH;
        }
    }

    lpb_context* created = nullptr;
    try
    {
        created = new (std::nothrow) lpb_context();
    }
    catch (...)
    {
        return LPB_RESULT_INTERNAL_ERROR;
    }

    if (created == nullptr)
    {
        return LPB_RESULT_INTERNAL_ERROR;
    }

    if (options != nullptr)
    {
        created->log_callback = options->log_callback;
        created->cancel_callback = options->cancel_callback;
        created->user_data = options->user_data;
    }

#if defined(LPB_NATIVE_TEST_HARNESS)
    created->test_context_id = g_next_test_context_id.fetch_add(1, std::memory_order_relaxed);
    if (created->test_context_id == 0)
    {
        created->test_context_id = g_next_test_context_id.fetch_add(1, std::memory_order_relaxed);
    }
#endif

    *context = created;
    log_message(created, LPB_LOG_DEBUG, "LivePhotoBox.Native context created.");
    return LPB_RESULT_OK;
}

void LPB_CALL lpb_destroy_context(lpb_context* context)
{
    if (context == nullptr)
    {
        return;
    }

    {
        std::unique_lock lock(context->lifetime_mutex);
        context->destroy_requested = true;
        context->lifetime_cv.wait(lock, [context] {
            return context->active_operations == 0;
        });
    }

    {
        std::scoped_lock lock(context->plan_mutex);
        for (auto& record : context->extraction_plans)
        {
            close_published_artifact_handles(record);
        }
    }

#if defined(LPB_NATIVE_TEST_HARNESS)
    test_archive_destroyed_context(context);
#endif

    log_message(context, LPB_LOG_DEBUG, "LivePhotoBox.Native context destroyed.");
    delete context;
}

#if defined(LPB_NATIVE_TEST_HARNESS)
uint64_t test_context_id(const lpb_context* context) noexcept
{
    return context == nullptr ? 0 : context->test_context_id;
}

bool test_get_plan_accounting(
    lpb_context* context,
    lpb_test_plan_accounting& accounting) noexcept
{
    accounting = {};
    if (context == nullptr)
    {
        return false;
    }

    try
    {
        std::scoped_lock lock(context->plan_mutex);
        accounting.context_id = context->test_context_id;
        accounting.issued = context->test_issued;
        accounting.claimed = context->test_claimed;
        accounting.consumed = context->test_consumed;
        accounting.released = context->test_released;
        accounting.registry_record_count = context->extraction_plans.size();
        accounting.live_plan_count = 0;
        for (const auto& record : context->extraction_plans)
        {
            if (is_live_plan_state(record.state))
            {
                ++accounting.live_plan_count;
            }
        }
        accounting.context_destroyed = 0;
        return true;
    }
    catch (...)
    {
        accounting = {};
        return false;
    }
}

bool test_get_destroyed_plan_accounting(
    uint64_t context_id,
    lpb_test_plan_accounting& accounting) noexcept
{
    accounting = {};
    if (context_id == 0)
    {
        return false;
    }

    try
    {
        std::scoped_lock lock(g_destroyed_context_archive_mutex);
        for (const auto& archive : g_destroyed_context_archives)
        {
            if (archive.accounting.context_id == context_id)
            {
                accounting = archive.accounting;
                return true;
            }
        }
    }
    catch (...)
    {
        accounting = {};
    }
    return false;
}

lpb_result test_probe_destroyed_plan(
    uint64_t context_id,
    uint64_t plan_token) noexcept
{
    if (context_id == 0 || plan_token == 0)
    {
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    try
    {
        std::scoped_lock lock(g_destroyed_context_archive_mutex);
        for (const auto& archive : g_destroyed_context_archives)
        {
            if (archive.accounting.context_id != context_id)
            {
                continue;
            }
            for (uint64_t token : archive.plan_tokens)
            {
                if (token == plan_token)
                {
                    return LPB_RESULT_PLAN_REPLAYED;
                }
            }
            return LPB_RESULT_AUTHORITY_VIOLATION;
        }
    }
    catch (...)
    {
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return LPB_RESULT_AUTHORITY_VIOLATION;
}

void test_archive_destroyed_context(lpb_context* context) noexcept
{
    if (context == nullptr)
    {
        return;
    }

    try
    {
        lpb_test_destroyed_context_archive archive{};
        {
            std::scoped_lock lock(context->plan_mutex);
            archive.accounting.context_id = context->test_context_id;
            archive.accounting.issued = context->test_issued;
            archive.accounting.claimed = context->test_claimed;
            archive.accounting.consumed = context->test_consumed;
            archive.accounting.registry_record_count = context->extraction_plans.size();
            archive.accounting.live_plan_count = 0;
            for (auto& record : context->extraction_plans)
            {
                archive.plan_tokens.push_back(record.token);
                if (is_live_plan_state(record.state))
                {
                    ++archive.accounting.live_plan_count;
                }
                if (record.state != lpb_plan_state::Released)
                {
                    record.state = lpb_plan_state::Released;
                    ++context->test_released;
                    ++archive.accounting.released_on_context_destroy;
                }
            }
            archive.accounting.released = context->test_released;
            archive.accounting.live_plan_count = 0;
            archive.accounting.registry_record_count = 0;
            archive.accounting.context_destroyed = 1;
        }

        std::scoped_lock archive_lock(g_destroyed_context_archive_mutex);
        g_destroyed_context_archives.push_back(std::move(archive));
    }
    catch (...)
    {
        // Test accounting must never change production destruction semantics.
    }
}
#endif

lpb_result LPB_CALL lpb_get_runtime_info(
    lpb_context* context,
    lpb_runtime_info* info)
{
    if (context == nullptr || info == nullptr)
    {
        set_error(context, "Context and runtime info are required.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    if (info->struct_size < sizeof(lpb_runtime_info))
    {
        set_error(context, "Runtime info structure is too small.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    info->abi_version = LPB_NATIVE_ABI_VERSION;
    info->capabilities = LPB_CAPABILITY_FOUNDATION | LPB_CAPABILITY_VIVO_LEGACY | LPB_CAPABILITY_HUAWEI_HONOR | LPB_CAPABILITY_SAMSUNG_JPEG | LPB_CAPABILITY_SAMSUNG_HEIC;
    return LPB_RESULT_OK;
}

lpb_result LPB_CALL lpb_context_check_cancelled(lpb_context* context)
{
    if (context == nullptr)
    {
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    int32_t cancelled = 0;
    try
    {
        if (context->cancel_callback != nullptr)
        {
            cancelled = context->cancel_callback(context->user_data);
        }
    }
    catch (...)
    {
        set_error(context, "A native cancellation callback failed.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    if (cancelled != 0)
    {
        set_error(context, "The native operation was cancelled.");
        return LPB_RESULT_CANCELLED;
    }

    return LPB_RESULT_OK;
}

lpb_result LPB_CALL lpb_get_last_error(
    lpb_context* context,
    char* utf8_buffer,
    size_t buffer_size,
    size_t* required_size)
{
    if (context == nullptr || required_size == nullptr)
    {
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    try
    {
        std::scoped_lock lock(context->error_mutex);
        const size_t needed = context->last_error.size() + 1;
        *required_size = needed;

        if (utf8_buffer == nullptr || buffer_size < needed)
        {
            return LPB_RESULT_BUFFER_TOO_SMALL;
        }

        std::memcpy(utf8_buffer, context->last_error.c_str(), needed);
        return LPB_RESULT_OK;
    }
    catch (...)
    {
        return LPB_RESULT_INTERNAL_ERROR;
    }
}

lpb_result LPB_CALL lpb_get_last_inspection_status(
    lpb_context* context, lpb_inspection_status* status)
{
    if (context == nullptr || status == nullptr) return LPB_RESULT_INVALID_ARGUMENT;
    try {
        std::scoped_lock lock(context->error_mutex);
        if (status->struct_size < sizeof(lpb_inspection_status)) return LPB_RESULT_INVALID_ARGUMENT;
        *status = context->inspection_status;
        return LPB_RESULT_OK;
    } catch (...) { return LPB_RESULT_INTERNAL_ERROR; }
}
