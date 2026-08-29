#ifndef LIVEPHOTOBOX_NATIVE_H
#define LIVEPHOTOBOX_NATIVE_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#define LPB_CALL __cdecl
#if defined(LPB_NATIVE_BUILD)
#define LPB_API __declspec(dllexport)
#else
#define LPB_API __declspec(dllimport)
#endif
#else
#define LPB_CALL
#define LPB_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define LPB_NATIVE_ABI_VERSION 1u

typedef struct lpb_context lpb_context;

typedef enum lpb_result
{
    LPB_RESULT_OK = 0,
    LPB_RESULT_INVALID_ARGUMENT = 1,
    LPB_RESULT_ABI_MISMATCH = 2,
    LPB_RESULT_CANCELLED = 3,
    LPB_RESULT_BUFFER_TOO_SMALL = 4,
    LPB_RESULT_INTERNAL_ERROR = 5
} lpb_result;

typedef enum lpb_log_level
{
    LPB_LOG_TRACE = 0,
    LPB_LOG_DEBUG = 1,
    LPB_LOG_INFO = 2,
    LPB_LOG_WARNING = 3,
    LPB_LOG_ERROR = 4
} lpb_log_level;

typedef void(LPB_CALL* lpb_log_callback)(
    void* user_data,
    lpb_log_level level,
    const char* utf8_message,
    size_t message_length);

typedef int32_t(LPB_CALL* lpb_cancel_callback)(void* user_data);

typedef struct lpb_context_options
{
    uint32_t struct_size;
    uint32_t abi_version;
    lpb_log_callback log_callback;
    lpb_cancel_callback cancel_callback;
    void* user_data;
} lpb_context_options;

typedef struct lpb_runtime_info
{
    uint32_t struct_size;
    uint32_t abi_version;
    uint64_t capabilities;
} lpb_runtime_info;

enum lpb_capability
{
    LPB_CAPABILITY_FOUNDATION = 1ull << 0
};

LPB_API uint32_t LPB_CALL lpb_get_abi_version(void);
LPB_API const char* LPB_CALL lpb_get_version(void);

LPB_API lpb_result LPB_CALL lpb_create_context(
    const lpb_context_options* options,
    lpb_context** context);

LPB_API void LPB_CALL lpb_destroy_context(lpb_context* context);

LPB_API lpb_result LPB_CALL lpb_get_runtime_info(
    lpb_context* context,
    lpb_runtime_info* info);

LPB_API lpb_result LPB_CALL lpb_context_check_cancelled(lpb_context* context);

LPB_API lpb_result LPB_CALL lpb_get_last_error(
    lpb_context* context,
    char* utf8_buffer,
    size_t buffer_size,
    size_t* required_size);

#ifdef __cplusplus
}
#endif

#endif
