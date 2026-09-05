#include "foundation/internal.h"
#include <windows.h>

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

    log_message(context, LPB_LOG_DEBUG, "LivePhotoBox.Native context destroyed.");
    delete context;
}

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
