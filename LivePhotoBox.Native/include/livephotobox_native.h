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

#define LPB_NATIVE_ABI_VERSION 2u

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
 * Patches an MP4 file's ftyp brand and (c)too atom in-place for Huawei compatibility.
 * Modifies the data in-place without changing the size or offsets.
 */
LPB_API lpb_result LPB_CALL lpb_huawei_patch_mp4(
    lpb_context* context,
    uint8_t* data,
    size_t data_size);

/*
 * Locates the exact byte offset and length of an Exif item in a HEIF file's ISO-BMFF container.
 */
LPB_API lpb_result LPB_CALL lpb_heif_locate_exif_item(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    uint64_t* out_offset,
    uint64_t* out_length);

/*
 * Locates the exact byte offset and length of an XMP item in a HEIF file's ISO-BMFF container.
 * XMP items are identified by item_type 'mime' with content_type 'application/rdf+xml'.
 */
LPB_API lpb_result LPB_CALL lpb_heif_locate_xmp_item(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    uint64_t* out_offset,
    uint64_t* out_length);

LPB_API lpb_result LPB_CALL lpb_samsung_sef_parse(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    uint64_t* out_video_offset,
    uint64_t* out_video_size);

LPB_API int32_t LPB_CALL lpb_samsung_sef_has_tag(
    const uint8_t* input,
    size_t input_size,
    uint16_t target_marker);

/*
 * Builds a complete Samsung SEF trailer for appending after image data.
 * JPEG: returns [tag_data | SEFH/SEFT].
 * HEIC: returns [mpvd box containing video + sefd box with tag_data + SEFH/SEFT].
 * video_data: raw MP4 video bytes.
 * is_heic: non-zero for HEIC output, zero for JPEG.
 * image_size: retained for ABI compatibility; HEIC mpv2 offsets are relative
 * to the emitted mpvd box and therefore start at byte 8.
 */
LPB_API lpb_result LPB_CALL lpb_samsung_sef_build_trailer(
    lpb_context* context,
    const uint8_t* video_data,
    size_t video_size,
    int32_t is_heic,
    uint64_t image_size,
    uint8_t* output,
    size_t output_size,
    size_t* out_written);

LPB_API lpb_result LPB_CALL lpb_mp4_strip_uuid_box(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    const uint8_t* user_type_16,
    uint8_t* output,
    size_t output_size,
    size_t* out_written);

LPB_API lpb_result LPB_CALL lpb_mp4_strip_stsd_tracks(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    const char** key_fragments,
    size_t fragment_count,
    uint8_t* output,
    size_t output_size,
    size_t* out_written);

LPB_API lpb_result LPB_CALL lpb_mp4_strip_mdta_keys(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    const char** name_starts,
    size_t name_starts_count,
    const char** name_contains,
    size_t name_contains_count,
    const char** value_contains,
    size_t value_contains_count,
    uint8_t* output,
    size_t output_size,
    size_t* out_written);

LPB_API lpb_result LPB_CALL lpb_jpeg_inject_xmp(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    const uint8_t* xmp_xml,
    size_t xmp_xml_size,
    uint8_t* output,
    size_t output_size,
    size_t* out_written);

/*
 * Strips Apple Live Photo proprietary EXIF MakerNote entries (e.g. 0x0011, 0x0017, 0x002b)
 * in-place for both JPEG and HEIC files.
 * Returns LPB_RESULT_OK on success or if MakerNote does not exist.
 */
LPB_API lpb_result LPB_CALL lpb_apple_strip_live_photo_entries(
    lpb_context* context,
    uint8_t* data,
    size_t data_size);

/*
 * Selectively strips Apple Live Photo proprietary EXIF MakerNote entries (e.g. 0x0011, 0x0017, 0x0025, 0x002b)
 * in-place for both JPEG and HEIC files according to the supplied authorized_tags array.
 * Only tags present in authorized_tags are stripped; all other MakerNote tags are strictly preserved.
 * The stripped tags are returned in out_stripped_tags up to max_stripped_tags, and out_stripped_count
 * receives the total number of stripped tags.
 */
LPB_API lpb_result LPB_CALL lpb_apple_strip_live_photo_entries_selective(
    lpb_context* context,
    uint8_t* data,
    size_t data_size,
    const uint16_t* authorized_tags,
    size_t authorized_count,
    uint16_t* out_stripped_tags,
    size_t max_stripped_tags,
    size_t* out_stripped_count);

/*
 * Overwrites the Apple MakerNote in-place with a minimal 70-byte block containing
 * the provided content_id (UUID). Used during merge/split if MakerNote already exists.
 */
LPB_API lpb_result LPB_CALL lpb_apple_write_content_identifier(
    lpb_context* context,
    uint8_t* data,
    size_t data_size,
    const char* content_id);

/*
 * Injects a constructed MakerNote into a JPEG's APP1 Exif segment.
 * Adjusts all TIFF/ExifIFD pointers internally.
 */
LPB_API lpb_result LPB_CALL lpb_apple_inject_makernote_jpeg(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    const uint8_t* makernote,
    size_t makernote_size,
    uint8_t* output,
    size_t output_size,
    size_t* out_written);

/*
 * Injects a constructed MakerNote into a HEIC file's Exif item.
 * If the new Exif data is larger than the original item's extent, it relocates
 * the Exif item payload to the end of the mdat box.
 */
LPB_API lpb_result LPB_CALL lpb_apple_inject_makernote_heic(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    const uint8_t* makernote,
    size_t makernote_size,
    uint8_t* output,
    size_t output_size,
    size_t* out_written);

/**
 * Appends Apple mebx/ContentDescribes metadata tracks to an MP4/QuickTime file.
 */
LPB_API lpb_result LPB_CALL lpb_apple_append_mebx_tracks(
    lpb_context* context,
    const uint8_t* data, size_t data_size,
    double cover_seconds,
    uint8_t* output, size_t output_size, size_t* out_written);

/*
 * Appends Apple mebx/ContentDescribes metadata tracks and a QuickTime
 * ContentIdentifier metadata item to an MP4/QuickTime file.
 */
LPB_API lpb_result LPB_CALL lpb_apple_append_mebx_tracks_with_content_identifier(
    lpb_context* context,
    const uint8_t* data, size_t data_size,
    double cover_seconds,
    const char* content_id,
    uint8_t* output, size_t output_size, size_t* out_written);

/* ========================================================================= */
/* Phase 02A: Media Inspection, Extraction, and Native Media Conversion      */
/* ========================================================================= */

typedef enum lpb_image_container
{
    LPB_IMAGE_CONTAINER_UNKNOWN = 0,
    LPB_IMAGE_CONTAINER_JPEG = 1,
    LPB_IMAGE_CONTAINER_HEIC = 2
} lpb_image_container;

typedef enum lpb_video_container
{
    LPB_VIDEO_CONTAINER_UNKNOWN = 0,
    LPB_VIDEO_CONTAINER_MP4 = 1,
    LPB_VIDEO_CONTAINER_MOV = 2
} lpb_video_container;

typedef enum lpb_video_codec
{
    LPB_VIDEO_CODEC_UNKNOWN = 0,
    LPB_VIDEO_CODEC_COPY = 1,
    LPB_VIDEO_CODEC_H264 = 2,
    LPB_VIDEO_CODEC_HEVC = 3
} lpb_video_codec;

typedef enum lpb_source_protocol
{
    LPB_SOURCE_PROTOCOL_UNKNOWN = 0,
    LPB_SOURCE_PROTOCOL_NON_LIVE = 1,
    LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1 = 2,
    LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2 = 3,
    LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO = 4,
    LPB_SOURCE_PROTOCOL_VIVO_X300 = 5,
    LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL = 6,
    LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG = 7,
    LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC = 8,
    LPB_SOURCE_PROTOCOL_HUAWEI_MOVING_PHOTO = 9,
    LPB_SOURCE_PROTOCOL_HONOR_MOVING_PHOTO = 10,
    LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO = 11
} lpb_source_protocol;

typedef struct lpb_media_range
{
    uint64_t offset;
    uint64_t length;
} lpb_media_range;

typedef struct lpb_image_item_facts
{
    uint32_t struct_size;
    int32_t is_present;
    lpb_image_container container;
    uint32_t width;
    uint32_t height;
    lpb_media_range file_range;
} lpb_image_item_facts;

typedef struct lpb_video_item_facts
{
    uint32_t struct_size;
    int32_t is_present;
    lpb_video_container container;
    lpb_video_codec codec;
    uint32_t width;
    uint32_t height;
    int32_t rotation_degrees;
    double duration_seconds;
    double fps;
    int32_t has_audio;
    lpb_media_range file_range;
    int32_t source_index;
} lpb_video_item_facts;

typedef struct lpb_gainmap_item_facts
{
    uint32_t struct_size;
    int32_t is_present;
    lpb_image_container container;
    lpb_media_range file_range;
} lpb_gainmap_item_facts;

typedef struct lpb_timing_facts
{
    uint32_t struct_size;
    int64_t cover_timestamp_us;
    int64_t primary_timestamp_us;
    int32_t cover_frame_index;
    int32_t total_frames;
} lpb_timing_facts;

typedef struct lpb_source_media_facts
{
    uint32_t struct_size;
    lpb_source_protocol protocol;
    lpb_image_item_facts primary_image;
    lpb_video_item_facts motion_video;
    lpb_gainmap_item_facts gain_map;
    lpb_timing_facts timing;
    /* Validated source-only bytes after the extracted media ranges.  This is
       zero for formats without a protocol tail. */
    lpb_media_range protocol_tail_range;
    char pairing_identifier[128];
    uint8_t primary_sha256[32];
    uint8_t secondary_sha256[32];
    int32_t has_secondary_source;
} lpb_source_media_facts;

/*
 * High-level Native inspection of source media files.
 * Performs deep container parsing (JPEG APP segments, ISOBMFF box trees,
 * SEF trailers, MakerNotes, XMP) in memory/file streams to identify
 * protocol and calculate exact, non-overlapping media ranges.
 */
LPB_API lpb_result LPB_CALL lpb_inspect_media(
    lpb_context* context,
    const char* primary_path,
    const char* secondary_path,
    lpb_source_media_facts* out_facts);

/*
 * High-level Native extraction of media items into destination files.
 * Reads source files strictly read-only and writes slices/files directly.
 */
LPB_API lpb_result LPB_CALL lpb_extract_media(
    lpb_context* context,
    const char* primary_path,
    const char* secondary_path,
    const lpb_source_media_facts* facts,
    const char* output_image_path,
    const char* output_video_path,
    const char* output_gainmap_path);

typedef enum lpb_extractor_fault
{
    LPB_EXTRACTOR_FAULT_NONE = 0,
    LPB_EXTRACTOR_FAULT_DISK_FULL = 1,
    LPB_EXTRACTOR_FAULT_WRITE_FAIL = 2,
    LPB_EXTRACTOR_FAULT_PUBLISH_FAIL = 3,
    LPB_EXTRACTOR_FAULT_SHORT_READ = 4,
    LPB_EXTRACTOR_FAULT_FLUSH_DISK_FULL = 5,
    LPB_EXTRACTOR_FAULT_FLUSH_WRITE_FAIL = 6,
    LPB_EXTRACTOR_FAULT_CLEANUP_FAIL = 0x80
} lpb_extractor_fault;

typedef void(LPB_CALL* lpb_extractor_step_callback)(
    void* user_data,
    int32_t step,
    uint64_t bytes_processed);

/* Test-only hook for deterministic fault injection and mid-stream synchronization */
LPB_API lpb_result LPB_CALL lpb_test_set_extractor_fault(
    lpb_context* context,
    lpb_extractor_fault fault,
    int32_t target_artifact,
    uint64_t trigger_after_bytes,
    lpb_extractor_step_callback callback,
    void* user_data);

/* Test-only hook for verifying that cleaner consumes pinned in-memory snapshot and never reopens source path */
typedef void(LPB_CALL* lpb_cleaner_snapshot_callback)(void* user_data);

LPB_API lpb_result LPB_CALL lpb_test_set_cleaner_snapshot_hook(
    lpb_context* context,
    lpb_cleaner_snapshot_callback callback,
    void* user_data);

/* Test-only helper to verify native SHA-256 implementation against standard test vectors */
LPB_API lpb_result LPB_CALL lpb_test_sha256_buffer(
    const uint8_t* data,
    size_t length,
    uint8_t out_hash[32]);

/* Test-only helper to verify native SHA-256 streaming file hashing and error handling */
LPB_API lpb_result LPB_CALL lpb_test_sha256_file(
    void* file_handle,
    uint8_t out_hash[32]);

/*
 * Probes a video file natively (ISOBMFF box tree traversal: moov/trak/stsd/tkhd/stts/mvhd)
 * to populate format facts (dimensions, duration, fps, rotation, codec, audio).
 */
LPB_API lpb_result LPB_CALL lpb_probe_video(
    lpb_context* context,
    const char* video_path,
    lpb_video_item_facts* out_video_facts);

/*
 * Stream remuxes between MP4 and MOV containers natively (modifies ftyp and box headers
 * without re-encoding video/audio samples).
 */
LPB_API lpb_result LPB_CALL lpb_remux_video(
    lpb_context* context,
    const char* input_video_path,
    const char* output_video_path,
    lpb_video_container target_container);

/*
 * Converts image formats natively.
 * If target matches source container, performs structure copy (out_reencoded = 0).
 * If transcoding is required (e.g. HEIC <-> JPEG), uses WIC (Windows Imaging Component).
 */
LPB_API lpb_result LPB_CALL lpb_convert_image(
    lpb_context* context,
    const char* input_image_path,
    const char* output_image_path,
    lpb_image_container target_container,
    int32_t quality,
    int32_t* out_reencoded);

/*
 * Transcodes video natively.
 * If target codec is COPY or matches source codec, performs native stream remuxing.
 * Otherwise uses Windows Media Foundation (supporting hardware MFTs with software fallback).
 */
LPB_API lpb_result LPB_CALL lpb_transcode_video(
    lpb_context* context,
    const char* input_video_path,
    const char* output_video_path,
    lpb_video_container target_container,
    lpb_video_codec target_codec,
    int32_t crf,
    char* out_encoder_used,
    size_t encoder_buf_len);

typedef enum lpb_media_artifact_kind
{
    LPB_ARTIFACT_PRIMARY_IMAGE = 0,
    LPB_ARTIFACT_MOTION_VIDEO = 1,
    LPB_ARTIFACT_GAIN_MAP = 2,
    LPB_ARTIFACT_AUXILIARY_ITEM = 3
} lpb_media_artifact_kind;

typedef enum lpb_residue_structure_kind
{
    LPB_RESIDUE_XMP_PROPERTY = 0,
    LPB_RESIDUE_XMP_CONTAINER_ITEM = 1,
    LPB_RESIDUE_EXIF_MAKERNOTE_TAG = 2,
    LPB_RESIDUE_QUICKTIME_MDTA_KEY = 3,
    LPB_RESIDUE_QUICKTIME_METADATA_TRACK = 4,
    LPB_RESIDUE_ISOBMFF_BOX = 5,
    LPB_RESIDUE_HEIF_ITEM = 6,
    LPB_RESIDUE_HEIF_PROPERTY = 7,
    LPB_RESIDUE_SEF_ENTRY = 8,
    LPB_RESIDUE_PROTOCOL_TAIL_RANGE = 9,
    LPB_RESIDUE_UUID_BOX = 10
} lpb_residue_structure_kind;

typedef enum lpb_coordinate_space
{
    LPB_COORD_ORIGINAL_SOURCE_RANGE = 0,
    LPB_COORD_EXTRACTED_ARTIFACT_RANGE = 1,
    LPB_COORD_STRUCTURED_SELECTOR = 2
} lpb_coordinate_space;

typedef enum lpb_residue_removal_mode
{
    LPB_REMOVAL_DELETE = 0,
    LPB_REMOVAL_ZERO_FILL = 1,
    LPB_REMOVAL_REBUILD_CONTAINER = 2
} lpb_residue_removal_mode;

typedef struct lpb_confirmed_residue
{
    uint32_t struct_size;
    char residue_id[64];
    lpb_source_protocol owner_protocol;
    int32_t artifact_role;
    int32_t structure_kind;
    char selector[128];
    char expected_semantic[64];
    char expected_fingerprint[64];
    int32_t coordinate_space;
    int32_t removal_mode;
    int32_t required_after_extraction;
} lpb_confirmed_residue;

LPB_API lpb_result LPB_CALL lpb_inspect_media_with_residues(
    lpb_context* context,
    const char* primary_path,
    const char* secondary_path,
    lpb_source_media_facts* out_facts,
    lpb_confirmed_residue* out_residues,
    size_t residues_capacity,
    size_t* out_residues_count);

typedef struct lpb_cleanup_action
{
    uint32_t struct_size;
    char residue_id[64];
    lpb_source_protocol owner_protocol;
    int32_t artifact_role;
    int32_t structure_kind;
    char selector[128];
    char expected_semantic[64];
    char expected_fingerprint[64];
    int32_t removal_mode;
    int32_t is_mandatory;
} lpb_cleanup_action;

typedef struct lpb_removed_protocol_fact
{
    uint32_t struct_size;
    char protocol_name[64];
    char component[64];
    char description[128];
    char residue_id[64];
    int32_t artifact_role;
    int32_t structure_kind;
    char operation[64];
    char before_fingerprint[64];
    char after_status[64];
} lpb_removed_protocol_fact;

typedef struct lpb_cleanup_artifact_binding
{
    uint32_t struct_size;
    int32_t artifact_role;
    uint64_t expected_length;
    uint8_t expected_sha256[32];
    int32_t has_expected_sha256;
    int32_t _reserved;
} lpb_cleanup_artifact_binding;

/*
 * Strips vendor-specific Live/Motion Photo protocol metadata and container markers
 * from extracted media artifacts according to an explicit plan.
 */
LPB_API lpb_result LPB_CALL lpb_clean_source_protocol_with_plan(
    lpb_context* context,
    const lpb_source_media_facts* facts,
    const lpb_cleanup_action* actions,
    size_t action_count,
    const lpb_cleanup_artifact_binding* targets,
    size_t target_count,
    const char* input_image_path,
    const char* input_video_path,
    const char* output_image_path,
    const char* output_video_path,
    lpb_removed_protocol_fact* out_facts,
    size_t facts_capacity,
    size_t* out_facts_count);


#ifdef __cplusplus
}

static_assert(sizeof(lpb_media_range) == 16, "lpb_media_range size mismatch");
static_assert(sizeof(lpb_image_item_facts) == 40, "lpb_image_item_facts size mismatch");
static_assert(sizeof(lpb_video_item_facts) == 80, "lpb_video_item_facts size mismatch");
static_assert(sizeof(lpb_gainmap_item_facts) == 32, "lpb_gainmap_item_facts size mismatch");
static_assert(sizeof(lpb_timing_facts) == 32, "lpb_timing_facts size mismatch");
static_assert(sizeof(lpb_source_media_facts) == 408, "lpb_source_media_facts size mismatch");
static_assert(sizeof(lpb_confirmed_residue) == 348, "lpb_confirmed_residue size mismatch");
static_assert(sizeof(lpb_cleanup_action) == 344, "lpb_cleanup_action size mismatch");
static_assert(sizeof(lpb_cleanup_artifact_binding) == 56, "lpb_cleanup_artifact_binding size mismatch");
static_assert(sizeof(lpb_removed_protocol_fact) == 524, "lpb_removed_protocol_fact size mismatch");
static_assert(offsetof(lpb_video_item_facts, source_index) == 72, "lpb_video_item_facts.source_index offset mismatch");
static_assert(offsetof(lpb_source_media_facts, primary_sha256) == 336, "lpb_source_media_facts.primary_sha256 offset mismatch");
static_assert(offsetof(lpb_source_media_facts, secondary_sha256) == 368, "lpb_source_media_facts.secondary_sha256 offset mismatch");
static_assert(offsetof(lpb_source_media_facts, has_secondary_source) == 400, "lpb_source_media_facts.has_secondary_source offset mismatch");
#endif
#endif
