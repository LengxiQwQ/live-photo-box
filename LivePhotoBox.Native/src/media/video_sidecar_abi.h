#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef _WIN32
#define LPB_VIDEO_SIDECAR_CALL __cdecl
#else
#define LPB_VIDEO_SIDECAR_CALL
#endif

#define LPB_VIDEO_SIDECAR_ABI_MAJOR 1U
#define LPB_VIDEO_SIDECAR_ABI_MINOR 1U
#define LPB_VIDEO_SIDECAR_VERSION_CAPACITY 32U
#define LPB_VIDEO_SIDECAR_ENCODER_CAPACITY 32U

enum lpb_video_sidecar_status_code_v1
{
    LPB_VIDEO_SIDECAR_OK = 0,
    LPB_VIDEO_SIDECAR_INVALID_ARGUMENT = 1,
    LPB_VIDEO_SIDECAR_UNSUPPORTED = 2,
    LPB_VIDEO_SIDECAR_CANCELLED = 3,
    LPB_VIDEO_SIDECAR_IO_FAILURE = 4,
    LPB_VIDEO_SIDECAR_DECODE_FAILURE = 5,
    LPB_VIDEO_SIDECAR_ENCODE_FAILURE = 6,
    LPB_VIDEO_SIDECAR_MUX_FAILURE = 7,
    LPB_VIDEO_SIDECAR_ABI_MISMATCH = 8,
    LPB_VIDEO_SIDECAR_INTERNAL_ERROR = 9
};

enum lpb_video_sidecar_capability_v1
{
    LPB_VIDEO_SIDECAR_CAP_TRANSCODE = 1U << 0U,
    LPB_VIDEO_SIDECAR_CAP_H264 = 1U << 1U,
    LPB_VIDEO_SIDECAR_CAP_HEVC = 1U << 2U,
    LPB_VIDEO_SIDECAR_CAP_H264_10BIT = 1U << 3U,
    LPB_VIDEO_SIDECAR_CAP_HEVC_10BIT = 1U << 4U
};

enum lpb_video_sidecar_codec_v1
{
    LPB_VIDEO_SIDECAR_CODEC_H264 = 1,
    LPB_VIDEO_SIDECAR_CODEC_HEVC = 2
};

enum lpb_video_sidecar_request_flag_v1
{
    // Native has independently validated the source track and will transplant
    // its exact MEBX bytes/timing into the separately staged final container.
    LPB_VIDEO_SIDECAR_REQUEST_NATIVE_TRANSPLANTS_MEBX = 1U << 0U
};

enum lpb_video_sidecar_container_v1
{
    LPB_VIDEO_SIDECAR_CONTAINER_MOV = 1,
    LPB_VIDEO_SIDECAR_CONTAINER_MP4 = 2
};

enum lpb_video_sidecar_io_status_v1
{
    LPB_VIDEO_SIDECAR_IO_OK = 0,
    LPB_VIDEO_SIDECAR_IO_EOF = 1,
    LPB_VIDEO_SIDECAR_IO_CANCELLED = 2,
    LPB_VIDEO_SIDECAR_IO_ERROR = 3
};

typedef int32_t(LPB_VIDEO_SIDECAR_CALL* lpb_video_sidecar_read_v1)(
    void* context,
    uint8_t* destination,
    uint32_t capacity,
    uint32_t* bytes_read);
typedef int32_t(LPB_VIDEO_SIDECAR_CALL* lpb_video_sidecar_seek_v1)(
    void* context,
    int64_t offset,
    int32_t whence,
    int64_t* position);
typedef int32_t(LPB_VIDEO_SIDECAR_CALL* lpb_video_sidecar_size_v1)(
    void* context,
    uint64_t* size);
typedef int32_t(LPB_VIDEO_SIDECAR_CALL* lpb_video_sidecar_write_v1)(
    void* context,
    const uint8_t* source,
    uint32_t size,
    uint32_t* bytes_written);
typedef int32_t(LPB_VIDEO_SIDECAR_CALL* lpb_video_sidecar_cancelled_v1)(
    void* context,
    uint32_t* is_cancelled);

typedef struct lpb_video_sidecar_abi_info_v1
{
    uint32_t struct_size;
    uint32_t abi_major;
    uint32_t abi_minor;
    uint32_t capability_flags;
    uint32_t avformat_version;
    uint32_t avcodec_version;
    uint32_t avutil_version;
    uint32_t swscale_version;
    uint32_t maximum_pixel_depth;
    uint32_t reserved[3];
    char library_version[LPB_VIDEO_SIDECAR_VERSION_CAPACITY];
} lpb_video_sidecar_abi_info_v1;

typedef struct lpb_video_sidecar_io_v1
{
    uint32_t struct_size;
    uint32_t reserved;
    void* input_context;
    lpb_video_sidecar_read_v1 input_read;
    lpb_video_sidecar_seek_v1 input_seek;
    lpb_video_sidecar_size_v1 input_size;
    void* output_context;
    lpb_video_sidecar_write_v1 output_write;
    lpb_video_sidecar_seek_v1 output_seek;
    lpb_video_sidecar_size_v1 output_size;
    lpb_video_sidecar_cancelled_v1 is_cancelled;
    void* cancellation_context;
} lpb_video_sidecar_io_v1;

typedef struct lpb_video_sidecar_request_v1
{
    uint32_t struct_size;
    uint32_t target_codec;
    uint32_t target_container;
    int32_t crf;
    uint32_t flags;
    uint32_t reserved[3];
} lpb_video_sidecar_request_v1;

typedef struct lpb_video_sidecar_result_v1
{
    uint32_t struct_size;
    int32_t status;
    int32_t library_error;
    uint32_t selected_capability;
    uint32_t target_codec;
    uint32_t output_width;
    uint32_t output_height;
    uint32_t output_pixel_depth;
    uint64_t video_frames;
    uint64_t audio_packets;
    uint64_t other_packets;
    uint64_t output_bytes;
    int32_t color_range;
    int32_t color_primaries;
    int32_t color_transfer;
    int32_t color_matrix;
    int32_t time_base_numerator;
    int32_t time_base_denominator;
    uint32_t reserved[4];
    char encoder_name[LPB_VIDEO_SIDECAR_ENCODER_CAPACITY];
    char library_version[LPB_VIDEO_SIDECAR_VERSION_CAPACITY];
} lpb_video_sidecar_result_v1;

typedef int32_t(LPB_VIDEO_SIDECAR_CALL* lpb_video_sidecar_query_fn_v1)(
    lpb_video_sidecar_abi_info_v1* info);
typedef int32_t(LPB_VIDEO_SIDECAR_CALL* lpb_video_sidecar_transcode_fn_v1)(
    const lpb_video_sidecar_request_v1* request,
    const lpb_video_sidecar_io_v1* io,
    lpb_video_sidecar_result_v1* result);

#ifdef __cplusplus
static_assert(offsetof(lpb_video_sidecar_abi_info_v1, library_version) == 48U);
static_assert(offsetof(lpb_video_sidecar_request_v1, crf) == 12U);
static_assert(offsetof(lpb_video_sidecar_result_v1, video_frames) == 32U);
static_assert(offsetof(lpb_video_sidecar_io_v1, input_context) == 8U);
#endif
