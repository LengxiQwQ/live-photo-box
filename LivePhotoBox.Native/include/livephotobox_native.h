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
    LPB_CAPABILITY_FOUNDATION = 1ull << 0,
    LPB_CAPABILITY_GOOGLE_V1 = 1ull << 8,
    LPB_CAPABILITY_GOOGLE_V2 = 1ull << 9,
    LPB_CAPABILITY_OPPO = 1ull << 10,
    LPB_CAPABILITY_VIVO_X300 = 1ull << 11,
    LPB_CAPABILITY_VIVO_LEGACY = 1ull << 12,
    LPB_CAPABILITY_HUAWEI_HONOR = 1ull << 13,
    LPB_CAPABILITY_SAMSUNG_JPEG = 1ull << 14,
    LPB_CAPABILITY_SAMSUNG_HEIC = 1ull << 15,
    LPB_CAPABILITY_APPLE = 1ull << 16
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

/*
 * Builds a vivo <= X200 JPEG payload from the complete UTF-8 `vivo{...}` JSON.
 * When replace_existing is non-zero, the last existing vivo tail within the
 * final 2 MiB is removed before the new tail is appended.
 */
LPB_API lpb_result LPB_CALL lpb_vivo_rewrite_image_metadata(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    const uint8_t* vivo_json,
    size_t vivo_json_size,
    int32_t replace_existing,
    uint8_t* output,
    size_t output_size,
    size_t* required_size);

/*
 * Replaces top-level vivoMediaExtInfo uuid boxes and appends the new vivo
 * metadata box, preserving the Legacy writer's stco/co64 adjustment behavior.
 */
LPB_API lpb_result LPB_CALL lpb_vivo_rewrite_video_metadata(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    const uint8_t* vivo_json,
    size_t vivo_json_size,
    uint8_t* output,
    size_t output_size,
    size_t* required_size);

/*
 * Builds the 60-byte HUAWEI Moving Photo tail marker.
 * prefix should be null-terminated (e.g. "v6_f").
 */
LPB_API lpb_result LPB_CALL lpb_huawei_build_tail(
    lpb_context* context,
    int32_t cover_frame,
    int32_t total_frames,
    uint64_t mp4_size,
    int32_t original_cover_ms,
    int32_t original_duration_ms,
    const char* prefix,
    uint8_t* output,
    size_t output_size,
    size_t* required_size);

/*
 * Patches a HEIC file's ftyp box to include the "tmap" compatible brand.
 * Modifies the data in-place. Requires at least 16 bytes.
 */
LPB_API lpb_result LPB_CALL lpb_huawei_patch_heic_ftyp(
    lpb_context* context,
    uint8_t* data,
    size_t data_size);

/*
 * Patches an MP4 file's ftyp brand and ©too atom in-place for Huawei compatibility.
 * Modifies the data in-place without changing the size or offsets.
 */
LPB_API lpb_result LPB_CALL lpb_huawei_patch_mp4(
    lpb_context* context,
    uint8_t* data,
    size_t data_size);

#ifdef __cplusplus
}
#endif

#endif
